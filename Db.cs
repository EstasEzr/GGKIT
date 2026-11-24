using Microsoft.Data.Sqlite;

sealed class Db : IDisposable
{
    readonly SqliteConnection connection;
    SqliteTransaction? transaction;

    public Db(string path)
    {
        connection = new(new SqliteConnectionStringBuilder { DataSource = path, ForeignKeys = true }.ToString());
        connection.Open();
    }

    SqliteCommand Command(string sql, object?[] args)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        for (var i = 0; i < args.Length; i++) command.Parameters.AddWithValue("$" + i, args[i] ?? DBNull.Value);
        return command;
    }

    public int Run(string sql, params object?[] args)
    {
        using var command = Command(sql, args);
        return command.ExecuteNonQuery();
    }

    public long Insert(string sql, params object?[] args)
    {
        using var command = Command(sql + "; SELECT last_insert_rowid();", args);
        return (long)command.ExecuteScalar()!;
    }

    public List<Row> All(string sql, params object?[] args)
    {
        using var command = Command(sql, args);
        using var reader = command.ExecuteReader();
        var rows = new List<Row>();
        while (reader.Read())
        {
            var row = new Row();
            for (var i = 0; i < reader.FieldCount; i++)
                row.Add(reader.GetName(i), reader.IsDBNull(i) ? null : reader.GetValue(i));
            rows.Add(row);
        }
        return rows;
    }

    public Row? One(string sql, params object?[] args) => All(sql, args).FirstOrDefault();
    public T Write<T>(Func<T> action)
    {
        using var current = connection.BeginTransaction();
        transaction = current;
        try
        {
            var result = action();
            current.Commit();
            return result;
        }
        finally { transaction = null; }
    }

    public void Backup(string destination)
    {
        using var target = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = destination }.ToString());
        target.Open();
        connection.BackupDatabase(target);
    }

    public void Dispose() => connection.Dispose();
}

sealed class Row : Dictionary<string, object?>
{
    public string S(string key) => this[key]?.ToString() ?? "";
    public long N(string key) => this[key] is long value ? value : 0;
    public bool Has(string role) => S("roles").Split(',').Contains(role);
}