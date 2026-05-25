using System.Globalization;

namespace Sqlity.Query;

internal sealed class SqlParser
{
    private readonly IReadOnlyList<SqlToken> _tokens;
    private int _position;

    public SqlParser(string sql)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sql);
        _tokens = SqlTokenizer.Tokenize(sql);
    }

    public SqlStatement ParseStatement()
    {
        SqlStatement statement = Peek().Kind switch
        {
            SqlTokenKind.Create => ParseCreateTable(),
            SqlTokenKind.Insert => ParseInsert(),
            SqlTokenKind.Select => ParseSelect(),
            _ => throw new InvalidOperationException($"Unsupported SQL statement starting with token '{Peek().Lexeme}'.")
        };

        Match(SqlTokenKind.Semicolon);
        Expect(SqlTokenKind.EndOfInput);
        return statement;
    }

    private CreateTableStatement ParseCreateTable()
    {
        Expect(SqlTokenKind.Create);
        Expect(SqlTokenKind.Table);
        var tableName = ExpectIdentifier("Expected a table name after CREATE TABLE.");
        Expect(SqlTokenKind.OpenParen);

        var columns = new List<ColumnSpecification>();
        do
        {
            var columnName = ExpectIdentifier("Expected a column name in the CREATE TABLE column list.");
            var typeName = ExpectIdentifier("Expected a type name after the column name.");
            var isPrimaryKey = false;

            if (Match(SqlTokenKind.Primary))
            {
                Expect(SqlTokenKind.Key);
                isPrimaryKey = true;
            }

            columns.Add(new ColumnSpecification(columnName, typeName, isPrimaryKey));
        }
        while (Match(SqlTokenKind.Comma));

        Expect(SqlTokenKind.CloseParen);
        return new CreateTableStatement(tableName, columns);
    }

    private InsertStatement ParseInsert()
    {
        Expect(SqlTokenKind.Insert);
        Expect(SqlTokenKind.Into);
        var tableName = ExpectIdentifier("Expected a table name after INSERT INTO.");

        List<string>? columns = null;
        if (Match(SqlTokenKind.OpenParen))
        {
            columns = new List<string>();
            do
            {
                columns.Add(ExpectIdentifier("Expected a column name in the INSERT column list."));
            }
            while (Match(SqlTokenKind.Comma));

            Expect(SqlTokenKind.CloseParen);
        }

        Expect(SqlTokenKind.Values);
        Expect(SqlTokenKind.OpenParen);

        var values = new List<SqlLiteral>();
        do
        {
            values.Add(ParseLiteral());
        }
        while (Match(SqlTokenKind.Comma));

        Expect(SqlTokenKind.CloseParen);
        return new InsertStatement(tableName, columns, values);
    }

    private SelectStatement ParseSelect()
    {
        Expect(SqlTokenKind.Select);

        IReadOnlyList<string>? columns;
        if (Match(SqlTokenKind.Star))
        {
            columns = null;
        }
        else
        {
            var selectedColumns = new List<string>();
            do
            {
                selectedColumns.Add(ExpectIdentifier("Expected a column name in the SELECT list."));
            }
            while (Match(SqlTokenKind.Comma));

            columns = selectedColumns;
        }

        Expect(SqlTokenKind.From);
        var tableName = ExpectIdentifier("Expected a table name after FROM.");

        PrimaryKeyFilter? filter = null;
        if (Match(SqlTokenKind.Where))
        {
            var columnName = ExpectIdentifier("Expected a column name after WHERE.");
            Expect(SqlTokenKind.Equals);
            filter = new PrimaryKeyFilter(columnName, ParseLiteral());
        }

        return new SelectStatement(tableName, columns, filter);
    }

    private SqlLiteral ParseLiteral()
    {
        var token = Advance();

        return token.Kind switch
        {
            SqlTokenKind.IntegerLiteral => new SqlLiteral(long.Parse(token.Lexeme, CultureInfo.InvariantCulture)),
            SqlTokenKind.StringLiteral => new SqlLiteral(token.Value ?? string.Empty),
            SqlTokenKind.BlobLiteral => new SqlLiteral((byte[])(token.Value ?? Array.Empty<byte>())),
            SqlTokenKind.True => new SqlLiteral(true),
            SqlTokenKind.False => new SqlLiteral(false),
            _ => throw new InvalidOperationException($"Expected a literal value, but found '{token.Lexeme}'.")
        };
    }

    private bool Match(SqlTokenKind kind)
    {
        if (Peek().Kind != kind)
        {
            return false;
        }

        _position++;
        return true;
    }

    private void Expect(SqlTokenKind kind)
    {
        var token = Advance();
        if (token.Kind != kind)
        {
            throw new InvalidOperationException($"Expected {kind}, but found '{token.Lexeme}'.");
        }
    }

    private string ExpectIdentifier(string errorMessage)
    {
        var token = Advance();
        if (token.Kind != SqlTokenKind.Identifier)
        {
            throw new InvalidOperationException(errorMessage);
        }

        return token.Lexeme;
    }

    private SqlToken Peek() => _tokens[_position];

    private SqlToken Advance()
    {
        var token = Peek();
        _position++;
        return token;
    }
}

internal abstract record SqlStatement;

internal sealed record CreateTableStatement(string TableName, IReadOnlyList<ColumnSpecification> Columns) : SqlStatement;

internal sealed record InsertStatement(string TableName, IReadOnlyList<string>? Columns, IReadOnlyList<SqlLiteral> Values) : SqlStatement;

internal sealed record SelectStatement(string TableName, IReadOnlyList<string>? Columns, PrimaryKeyFilter? Filter) : SqlStatement;

internal sealed record ColumnSpecification(string Name, string TypeName, bool IsPrimaryKey);

internal sealed record PrimaryKeyFilter(string ColumnName, SqlLiteral Value);

internal sealed record SqlLiteral(object Value);
