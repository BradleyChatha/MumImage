open System
open SdlangSharp
open Microsoft.Data.Sqlite
open System.IO
open System.Text.RegularExpressions
open System.Diagnostics
open System.Text

let getDb =
    let conn = new SqliteConnection("Data Source=index.db")
    conn.Open ()

    let command = conn.CreateCommand ()
    command.CommandText <- @"
        CREATE TABLE IF NOT EXISTS files (
            keyword TEXT NOT NULL,
            path TEXT NOT NULL
        );
        CREATE INDEX IF NOT EXISTS idx_files_keyword ON files (keyword);
        CREATE UNIQUE INDEX IF NOT EXISTS idx_files_unique ON files (keyword, path);
    "
    command.ExecuteNonQuery () |> ignore

    conn

let keywordRegex = Regex(@"([a-zA-Z0-9]+)")

type InsertCommand = {
    Com : SqliteCommand
    Keyword : SqliteParameter
    Path : SqliteParameter
}

let getInsertCommand (db : SqliteConnection) =
    let com = db.CreateCommand ()
    com.CommandText <- @"
        INSERT OR IGNORE INTO files (keyword, path) VALUES ($keyword, $path);
    "
    {
        Com = com
        Keyword = com.Parameters.Add ("$keyword", SqliteType.Text)
        Path = com.Parameters.Add ("$path", SqliteType.Text)
    }

let getKeywords filename =
    keywordRegex.Matches filename 
    |> Seq.filter (fun m -> m.Success)
    |> Seq.map (fun m -> m.Captures.[0].Value)

let rec rebuildIndex db dir =
    for subdir in (Directory.EnumerateDirectories dir) do
        rebuildIndex db subdir

    let insertCommand = getInsertCommand db
    for file in (Directory.EnumerateFiles dir) do
        for keyword in (getKeywords (Path.GetFileName file)) do
            insertCommand.Keyword.Value <- keyword
            insertCommand.Path.Value <- file
            insertCommand.Com.ExecuteNonQuery () |> ignore

let getSdl =
    if File.Exists ("./cats.sdl") then
        let text = File.ReadAllText("./cats.sdl")
        let reader = SdlReader(text.AsSpan())
        let sdl = reader.ToAst()
        let map = Collections.Generic.List<string * string>()
        for child in sdl.Children do
            for value in child.Values do
                map.Add((child.Name, value.String))
        map
    else
        Collections.Generic.List<string * string>()

let doReflect (db : SqliteConnection) =
    let command = db.CreateCommand ()
    command.CommandText <- @"
        SELECT * FROM files;
    "

    if not (Directory.Exists (Path.Combine ("reflected"))) then
        Directory.CreateDirectory "reflected" |> ignore
    if not (Directory.Exists (Path.Combine ("reflected", "keywords"))) then
        Directory.CreateDirectory "reflected/keywords" |> ignore

    let reader = command.ExecuteReader ()
    let sdl    = getSdl
    while reader.Read() do
        let keyword = reader.GetString 0
        let path    = reader.GetString 1
        let dir     = Path.Combine ("reflected", "keywords", keyword)
        let file    = Path.Combine (dir, Path.GetFileName path)
        if not (Directory.Exists dir) then
            Directory.CreateDirectory dir |> ignore

        // This is just for mum, no need for it to be perfect code.
        for (cat, _) in (sdl |> Seq.filter (fun (_, key) -> key = keyword)) do
            let catDir = Path.Combine ("reflected", "categories", cat)
            let catFile = Path.Combine (catDir, Path.GetFileName path)
            if not (Directory.Exists catDir) then
                Directory.CreateDirectory catDir |> ignore
            if OperatingSystem.IsWindows () then
                Process.Start ("cmd.exe", ["/C"; "mklink"; catFile; path]) |> ignore
            elif OperatingSystem.IsLinux () then
                Process.Start ("/bin/bash", ["-c"; $"ln -s '{path}' '{catFile}'"]) |> ignore
            else
                failwith "Unsupported operating system."

        if OperatingSystem.IsWindows () then
            Process.Start ("cmd.exe", ["/C"; "mklink"; file; path]) |> ignore
        elif OperatingSystem.IsLinux () then
            Process.Start ("/bin/bash", ["-c"; $"ln -s '{path}' '{file}'"]) |> ignore
        else
            failwith "Unsupported operating system."

let createCsv (db : SqliteConnection) =
    let command = db.CreateCommand ()
    command.CommandText <- @"
        SELECT keyword, COUNT(keyword)
        FROM files
        GROUP BY keyword;
    "

    let builder = StringBuilder ()
    builder.AppendLine "keyword,count" |> ignore

    let reader = command.ExecuteReader ()
    while reader.Read () do
        builder.AppendLine ($"{reader.GetString 0},{reader.GetInt64 1}") |> ignore

    File.WriteAllText ("keywords.csv", builder.ToString ())

[<EntryPoint>]
let main argv =
    let command = if argv.Length < 1 then "<none given>" else argv.[0]
    let path = if argv.Length < 2 then "./" else argv.[1]

    let db = getDb
    match command with
    | "index" ->
        let transaction = db.BeginTransaction ()
        rebuildIndex db path
        transaction.Commit ()
    | "reflect" ->
        doReflect db
    | "csv" ->
        createCsv db
    | _ -> failwith $"Unknown command '{command}'"
    0