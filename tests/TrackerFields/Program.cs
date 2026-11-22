using System.Globalization;
using JournalDesk;
using Microsoft.Data.Sqlite;

static void Require(bool ok, string message)
{
    if (!ok)
        throw new InvalidOperationException(message);
}

string folder = Directory.CreateTempSubdirectory("JournalDesk-TrackerFields-").FullName;
try
{
    string dbPath = Path.Combine(folder, "values.db");
    using (var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = dbPath, Pooling = false }.ToString()))
    {
        db.Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE UsnJrnl(a,b,c,d,e);
            INSERT INTO UsnJrnl VALUES(NULL,'(NULL)',0,'',X'00FF80');
            INSERT INTO UsnJrnl VALUES('00FF80',9007199254740993,12.5,X'',NULL);
            """;
        cmd.ExecuteNonQuery();
    }
    using (var results = TrackerResult.Load(dbPath, CancellationToken.None))
    {
        var table = results.Tables.Single();
        var page = results.Read(table, "00ff80", 0, 20, CancellationToken.None);
        Require(page.Rows.Count == 2 && !page.HasMore, "Search the displayed BLOB and literal text.");
        var first = page.Rows[0];
        var second = page.Rows[1];
        Require(first.Select(c => c.Type).SequenceEqual(["NULL", "TEXT", "INTEGER", "TEXT", "BLOB"]), "Value storage classes.");
        Require(first[0].Value == null && first[1].Value == "(NULL)" && first[2].Value == "0" && first[3].Value == "", "Absence and literal values.");
        Require(first[4].Value == "00FF80" && second[0].Value == first[4].Value && second[0].Type != first[4].Type, "Preserve binary versus text.");
        Require(second[1].Value == "9007199254740993" && second[2].Type == "REAL" && second[3].Storage == "BLOB", "Integer precision, real and empty binary.");
    }
    string csvPath = Path.Combine(folder, "values.csv");
    File.WriteAllText(csvPath, "Name,Name,\r\n0001,\"first\r\nsecond\",\r\n");
    using (var results = TrackerResult.Load(csvPath, CancellationToken.None))
    {
        var table = results.Tables.Single();
        Require(table.DisplayHeaders.Distinct().Count() == 3 && table.Headers.SequenceEqual(["Name", "Name", ""]), "Identify columns while retaining their source headers.");
        var row = results.Read(table, "", 0, 1, CancellationToken.None).Rows.Single();
        Require(row[0].Value == "0001" && row[1].Value == "first\r\nsecond" && row[1].Preview.Contains('↵'), "Preserve CSV text and original line endings.");
        string output = Path.Combine(folder, "export.csv");
        Require(results.Export(table, "", output, CancellationToken.None) == 1, "Export one matching row.");
        Require(File.ReadAllText(output).StartsWith("\"Name\",\"Name\",\"\"\r\n\"0001\",\"first\r\nsecond\",\"\"", StringComparison.Ordinal), "Display labels must not replace exported values.");
    }
    string sortDb = Path.Combine(folder, "sort.db");
    using (var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = sortDb, Pooling = false }.ToString()))
    {
        db.Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = """"
            CREATE TABLE Sorted("Value, with ""quotes""" INTEGER,"rowid" INTEGER, scope TEXT);
            WITH RECURSIVE n(i) AS (SELECT 0 UNION ALL SELECT i+1 FROM n WHERE i<440)
            INSERT INTO Sorted SELECT i%7,i,CASE WHEN i=440 THEN 'excluded' ELSE 'included' END FROM n;
            """";
        cmd.ExecuteNonQuery();
    }
    using (var results = TrackerResult.Load(sortDb, CancellationToken.None))
    {
        var table = results.Tables.Single();
        var order = new TrackerOrder(0, true);
        var token = CancellationToken.None;
        long rank = results.LocateAfterSort(table, "included", 221, null, order, token);
        var page = results.Read(table, "included", (int)rank / 200, 200, token, order);
        Require(page.Rows[(int)rank % 200][1].Value == "221", "Sorting preserves the selected evidence row across pages.");
        string output = Path.Combine(folder, "sorted.csv");
        Require(results.Export(table, "included", output, token, order) == 440, "Export the same filtered scope.");
        var expected = Enumerable.Range(0, 440).OrderByDescending(i => i % 7).ToArray();
        Require(File.ReadLines(output).Skip(1).Select(line => int.Parse(line.Split(',')[1].Trim('"'), CultureInfo.InvariantCulture)).SequenceEqual(expected),
            "Sort numerically across all pages with stable ties and preserve that order on export.");
    }
    Console.WriteLine("Tracker field types, exact row identity, cross-page sorting, filtered CSV order and headers passed.");
}
finally
{
    foreach (var file in Directory.EnumerateFiles(folder))
        File.Delete(file);
    Directory.Delete(folder);
}
