using Xunit;

namespace OllamaMcpBridge.Tests;

public sealed class SqlSanitizerTests
{
    [Fact]
    public void Clean_sql_unchanged()
    {
        var s = "SELECT 1";
        var r = SqlSanitizer.Sanitize(s);
        Assert.True(r.Success);
        Assert.Equal("SELECT 1", r.Sql);
    }

    [Fact]
    public void Fenced_sql_extraction()
    {
        var s = "```sql\nSELECT 1\n```";
        var r = SqlSanitizer.Sanitize(s);
        Assert.True(r.Success);
        Assert.Equal("SELECT 1", r.Sql);
    }

    [Fact]
    public void Fenced_no_tag()
    {
        var s = "```\nSELECT 1\n```";
        var r = SqlSanitizer.Sanitize(s);
        Assert.True(r.Success);
        Assert.Equal("SELECT 1", r.Sql);
    }

    [Fact]
    public void Json_wrapper()
    {
        var s = """{"sql":"SELECT 1"}""";
        var r = SqlSanitizer.Sanitize(s);
        Assert.True(r.Success);
        Assert.Equal("SELECT 1", r.Sql);
    }

    [Fact]
    public void Leading_explanation_fails()
    {
        var s = "Here is the query: SELECT 1";
        var r = SqlSanitizer.Sanitize(s);
        Assert.False(r.Success);
    }

    [Fact]
    public void Delete_forbidden()
    {
        var s = "DELETE FROM t";
        var r = SqlSanitizer.Sanitize(s);
        Assert.False(r.Success);
    }

    [Fact]
    public void Postgres_cast_fails()
    {
        var s = "SELECT 1::int";
        var r = SqlSanitizer.Sanitize(s);
        Assert.False(r.Success);
    }

    [Fact]
    public void Multiple_semicolons_fails()
    {
        var s = "SELECT 1; SELECT 2";
        var r = SqlSanitizer.Sanitize(s);
        Assert.False(r.Success);
    }

    [Fact]
    public void Empty_fails()
    {
        var r = SqlSanitizer.Sanitize("   ");
        Assert.False(r.Success);
    }

    [Fact]
    public void Over_max_length_fails()
    {
        var s = "SELECT " + new string('x', SqlSanitizer.MaxSqlLength - 7 + 1);
        var r = SqlSanitizer.Sanitize(s);
        Assert.False(r.Success);
    }

    [Fact]
    public void With_cte_passes()
    {
        var s = "WITH t AS (SELECT 1 AS a) SELECT a FROM t";
        var r = SqlSanitizer.Sanitize(s);
        Assert.True(r.Success);
    }
}
