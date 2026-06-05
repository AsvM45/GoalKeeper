using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using ServiceEngine.Core;
using Xunit;

namespace ServiceEngine.Tests;

/// <summary>
/// Integration tests for the challenge-token flow:
///   TypingChallenge.xaml.cs generates a token → sends STORE_TOKEN to PipeServer
///   → PipeServer.ProcessMessageAsync stores it in the DB
///   → ArmSystem / ActivateNuclear sends the token → ConsumeTokenAsync validates it
/// This test uses a real SQLite DB (temp file) to verify the full round-trip.
/// </summary>
public class TokenFlowIntegrationTests : IDisposable
{
    private readonly string _dbPath;
    private readonly ScreenTimeLogger _db;

    public TokenFlowIntegrationTests()
    {
        _dbPath = Path.GetTempFileName();
        _db = new ScreenTimeLogger(_dbPath);
        _db.InitializeAsync().GetAwaiter().GetResult();
    }

    public void Dispose()
    {
        _db.Dispose();
        try { if (File.Exists(_dbPath)) File.Delete(_dbPath); } catch { }
    }

    [Fact]
    public async Task StoreAndConsume_SingleUse_Succeeds()
    {
        const string token = "abc123def456";

        await _db.StoreTokenAsync(token);

        bool first = await _db.ConsumeTokenAsync(token);
        Assert.True(first, "First consume should succeed.");
    }

    [Fact]
    public async Task ConsumeToken_SecondUse_ReturnsFalse()
    {
        const string token = "reuse-test-token";

        await _db.StoreTokenAsync(token);
        await _db.ConsumeTokenAsync(token); // consume it once

        bool second = await _db.ConsumeTokenAsync(token);
        Assert.False(second, "Second consume should fail (single-use).");
    }

    [Fact]
    public async Task ConsumeToken_UnknownToken_ReturnsFalse()
    {
        bool result = await _db.ConsumeTokenAsync("this-token-was-never-stored");
        Assert.False(result, "Unknown token must be rejected.");
    }

    [Fact]
    public async Task ConsumeToken_ExpiredToken_ReturnsFalse()
    {
        const string token = "expired-token";

        // Insert a token with a CreatedAt timestamp more than 5 minutes in the past
        // so ConsumeTokenAsync's >-5 minutes window rejects it.
        var connStr = $"Data Source={_dbPath};Default Timeout=5;";
        await using (var conn = new SqliteConnection(connStr))
        {
            await conn.OpenAsync();
            await using var cmd = new SqliteCommand(
                "INSERT INTO ChallengeTokens (Token, CreatedAt) VALUES (@t, datetime('now', '-10 minutes'))", conn);
            cmd.Parameters.AddWithValue("@t", token);
            await cmd.ExecuteNonQueryAsync();
        }

        bool result = await _db.ConsumeTokenAsync(token);
        Assert.False(result, "Expired token (>5 min old) must be rejected.");
    }

    [Fact]
    public async Task StoreToken_ThenConsumeMultipleTokens_EachOnlyOnce()
    {
        const string tokenA = "token-alpha";
        const string tokenB = "token-beta";

        await _db.StoreTokenAsync(tokenA);
        await _db.StoreTokenAsync(tokenB);

        // Both should work once
        Assert.True(await _db.ConsumeTokenAsync(tokenA));
        Assert.True(await _db.ConsumeTokenAsync(tokenB));

        // Neither should work again
        Assert.False(await _db.ConsumeTokenAsync(tokenA));
        Assert.False(await _db.ConsumeTokenAsync(tokenB));
    }
}
