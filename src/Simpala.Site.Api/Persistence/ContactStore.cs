using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Simpala.Site.Api.Persistence;

public enum ContactState { Pending, Sending, Accepted, Sent, Failed, Unknown }
public sealed record ContactRecord(string Company, string IdempotencyKey, string Fingerprint,
    string Registration, DateTimeOffset CreatedAt, ContactState State, string Mode = "Simulation");
public sealed class IdempotencyConflictException : Exception { }
public sealed class ChallengeConsumedException : Exception { }

public sealed class ContactStore(string path)
{
    private readonly string connectionString = new SqliteConnectionStringBuilder
    { DataSource = path, DefaultTimeout = 5, Pooling = false }.ToString();

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(connectionString);
        connection.Open();
        return connection;
    }

    public void Initialize()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA journal_mode=WAL;
            CREATE TABLE IF NOT EXISTS Requests (
                Company TEXT NOT NULL CHECK(Company IN ('financeira','consorcio')),
                IdempotencyKey TEXT NOT NULL,
                Fingerprint TEXT NOT NULL,
                Registration TEXT NOT NULL UNIQUE,
                CreatedAt TEXT NOT NULL,
                State TEXT NOT NULL CHECK(State IN ('Pending','Sending','Sent','Failed','Unknown')),
                Simulation INTEGER NOT NULL DEFAULT 1 CHECK(Simulation = 1),
                PRIMARY KEY (Company, IdempotencyKey)
            );
            CREATE TABLE IF NOT EXISTS UsedChallenges (
                Nonce TEXT PRIMARY KEY,
                ExpiresAt TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS IX_Requests_CreatedAt ON Requests(CreatedAt);
            """;
        command.ExecuteNonQuery();
        using var version = connection.CreateCommand();
        version.CommandText = "PRAGMA table_info(Requests)";
        using var columns = version.ExecuteReader();
        var migrated = false;
        while (columns.Read()) if (columns.GetString(1) == "Mode") migrated = true;
        columns.Close();
        if (migrated) return;
        using var transaction = connection.BeginTransaction();
        using var migration = connection.CreateCommand();
        migration.Transaction = transaction;
        migration.CommandText = """
            CREATE TABLE Requests_v2 (
                Company TEXT NOT NULL CHECK(Company IN ('financeira','consorcio')),
                IdempotencyKey TEXT NOT NULL,
                Fingerprint TEXT NOT NULL,
                Registration TEXT NOT NULL UNIQUE,
                CreatedAt TEXT NOT NULL,
                State TEXT NOT NULL CHECK(State IN ('Pending','Sending','Accepted','Sent','Failed','Unknown')),
                Mode TEXT NOT NULL CHECK(Mode IN ('Simulation','HML','Production')),
                AttemptStartedAt TEXT,
                PRIMARY KEY (Company,IdempotencyKey)
            );
            INSERT INTO Requests_v2(Company,IdempotencyKey,Fingerprint,Registration,CreatedAt,State,Mode)
            SELECT Company,IdempotencyKey,Fingerprint,Registration,CreatedAt,State,'Simulation' FROM Requests;
            DROP TABLE Requests;
            ALTER TABLE Requests_v2 RENAME TO Requests;
            CREATE INDEX IX_Requests_CreatedAt ON Requests(CreatedAt);
            PRAGMA user_version=2;
            """;
        migration.ExecuteNonQuery();
        transaction.Commit();
    }

    public ContactRecord? Find(string company, string key)
    {
        using var connection = Open();
        return Find(connection, null, company, key);
    }

    private static ContactRecord? Find(SqliteConnection connection, SqliteTransaction? transaction, string company, string key)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT Fingerprint,Registration,CreatedAt,State,Mode FROM Requests WHERE Company=$company AND IdempotencyKey=$key";
        command.Parameters.AddWithValue("$company", company);
        command.Parameters.AddWithValue("$key", key);
        using var reader = command.ExecuteReader();
        return reader.Read() ? new(company, key, reader.GetString(0), reader.GetString(1),
            DateTimeOffset.Parse(reader.GetString(2), CultureInfo.InvariantCulture),
            Enum.Parse<ContactState>(reader.GetString(3)), reader.GetString(4)) : null;
    }

    public ContactRecord CreateOrGet(string company, string key, string fingerprint, string nonce,
        DateTimeOffset challengeExpiry, DateTimeOffset now, string mode = "Simulation")
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        var existing = Find(connection, transaction, company, key);
        if (existing is not null)
        {
            if (existing.Fingerprint != fingerprint || existing.Mode != mode) throw new IdempotencyConflictException();
            transaction.Commit();
            return existing;
        }
        using (var challenge = connection.CreateCommand())
        {
            challenge.Transaction = transaction;
            challenge.CommandText = "INSERT INTO UsedChallenges(Nonce,ExpiresAt) VALUES($nonce,$expiry) ON CONFLICT(Nonce) DO NOTHING";
            challenge.Parameters.AddWithValue("$nonce", nonce);
            challenge.Parameters.AddWithValue("$expiry", challengeExpiry.ToString("O"));
            if (challenge.ExecuteNonQuery() == 0) throw new ChallengeConsumedException();
        }
        var prefix = company == "financeira" ? "FIN" : "CON";
        var day = TimeZoneInfo.ConvertTime(now, Services.EmailComposer.SaoPaulo).ToString("yyyyMMdd", CultureInfo.InvariantCulture);
        var registration = $"FORMS-{prefix}-{day}-{Guid.NewGuid():N}".ToUpperInvariant();
        using var insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText = """
            INSERT INTO Requests(Company,IdempotencyKey,Fingerprint,Registration,CreatedAt,State,Mode)
            VALUES($company,$key,$hash,$registration,$created,'Pending',$mode)
            """;
        insert.Parameters.AddWithValue("$company", company);
        insert.Parameters.AddWithValue("$key", key);
        insert.Parameters.AddWithValue("$hash", fingerprint);
        insert.Parameters.AddWithValue("$registration", registration);
        insert.Parameters.AddWithValue("$created", now.ToString("O"));
        insert.Parameters.AddWithValue("$mode", mode);
        insert.ExecuteNonQuery();
        transaction.Commit();
        return new(company, key, fingerprint, registration, now, ContactState.Pending, mode);
    }

    // A committed claim precedes the network request. Only one concurrent caller can claim it.
    public bool TryStart(ContactRecord record, DateTimeOffset now)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE Requests SET State='Sending',AttemptStartedAt=$now
            WHERE Company=$company AND IdempotencyKey=$key AND State='Pending' AND Mode=$mode AND Mode<>'Simulation' AND Fingerprint=$hash
            """;
        command.Parameters.AddWithValue("$now", now.ToString("O"));
        command.Parameters.AddWithValue("$company", record.Company);
        command.Parameters.AddWithValue("$key", record.IdempotencyKey);
        command.Parameters.AddWithValue("$mode", record.Mode);
        command.Parameters.AddWithValue("$hash", record.Fingerprint);
        return command.ExecuteNonQuery() == 1;
    }

    // Editing is safe only before any mail attempt. The old payload can no longer acquire the claim.
    public ContactRecord ReplacePending(ContactRecord record, string fingerprint, string nonce,
        DateTimeOffset expiry)
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        using var update = connection.CreateCommand();
        update.Transaction = transaction;
        update.CommandText = """
            UPDATE Requests SET Fingerprint=$hash WHERE Company=$company AND IdempotencyKey=$key
            AND State='Pending' AND Mode=$mode AND Mode<>'Simulation' AND Fingerprint=$old
            """;
        update.Parameters.AddWithValue("$hash", fingerprint);
        update.Parameters.AddWithValue("$old", record.Fingerprint);
        update.Parameters.AddWithValue("$company", record.Company);
        update.Parameters.AddWithValue("$key", record.IdempotencyKey);
        update.Parameters.AddWithValue("$mode", record.Mode);
        if (update.ExecuteNonQuery() != 1) throw new IdempotencyConflictException();
        using var challenge = connection.CreateCommand();
        challenge.Transaction = transaction;
        challenge.CommandText = "INSERT INTO UsedChallenges(Nonce,ExpiresAt) VALUES($nonce,$expiry) ON CONFLICT(Nonce) DO NOTHING";
        challenge.Parameters.AddWithValue("$nonce", nonce);
        challenge.Parameters.AddWithValue("$expiry", expiry.ToString("O"));
        if (challenge.ExecuteNonQuery() != 1) throw new ChallengeConsumedException();
        transaction.Commit();
        return record with { Fingerprint = fingerprint };
    }

    public void Complete(ContactRecord record, ContactState state)
    {
        if (state is not (ContactState.Accepted or ContactState.Failed or ContactState.Unknown))
            throw new ArgumentOutOfRangeException(nameof(state));
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE Requests SET State=$state
            WHERE Company=$company AND IdempotencyKey=$key AND State='Sending' AND Mode=$mode
            """;
        command.Parameters.AddWithValue("$state", state.ToString());
        command.Parameters.AddWithValue("$company", record.Company);
        command.Parameters.AddWithValue("$key", record.IdempotencyKey);
        command.Parameters.AddWithValue("$mode", record.Mode);
        command.ExecuteNonQuery();
    }

    public void MarkStale(DateTimeOffset now)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE Requests SET State='Unknown' WHERE State='Sending' AND AttemptStartedAt < $cutoff";
        command.Parameters.AddWithValue("$cutoff", now.AddMinutes(-2).ToString("O"));
        command.ExecuteNonQuery();
    }

    public int Purge(DateTimeOffset cutoff, DateTimeOffset now)
    {
        MarkStale(now);
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM Requests WHERE CreatedAt < $cutoff AND State NOT IN ('Sending','Unknown')";
        command.Parameters.AddWithValue("$cutoff", cutoff.ToString("O"));
        var removed = command.ExecuteNonQuery();
        command.CommandText = "DELETE FROM UsedChallenges WHERE ExpiresAt < $now";
        command.Parameters.Clear();
        command.Parameters.AddWithValue("$now", now.ToString("O"));
        command.ExecuteNonQuery();
        return removed;
    }
}
