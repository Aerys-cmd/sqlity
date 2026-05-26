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
            SqlTokenKind.Update => ParseUpdate(),
            SqlTokenKind.Delete => ParseDelete(),
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

        IReadOnlyList<ColumnReference>? columns;
        if (Match(SqlTokenKind.Star))
        {
            columns = null;
        }
        else
        {
            var selectedColumns = new List<ColumnReference>();
            do
            {
                selectedColumns.Add(ParseColumnReference("Expected a column name in the SELECT list."));
            }
            while (Match(SqlTokenKind.Comma));

            columns = selectedColumns;
        }

        Expect(SqlTokenKind.From);
        var tableName = ExpectIdentifier("Expected a table name after FROM.");

        var joins = new List<JoinClause>();
        while (Peek().Kind is SqlTokenKind.Inner or SqlTokenKind.Left or SqlTokenKind.Join)
        {
            joins.Add(ParseJoinClause());
        }

        WhereExpression? filter = null;
        if (Match(SqlTokenKind.Where))
        {
            filter = ParseWhereExpression();
        }

        return new SelectStatement(tableName, columns, filter, joins);
    }

    private JoinClause ParseJoinClause()
    {
        JoinType joinType;
        if (Match(SqlTokenKind.Inner))
        {
            Expect(SqlTokenKind.Join);
            joinType = JoinType.Inner;
        }
        else if (Match(SqlTokenKind.Left))
        {
            Expect(SqlTokenKind.Join);
            joinType = JoinType.Left;
        }
        else
        {
            Expect(SqlTokenKind.Join);
            joinType = JoinType.Inner;
        }

        var joinTableName = ExpectIdentifier("Expected a table name after JOIN.");
        Expect(SqlTokenKind.On);

        var leftTable = ExpectIdentifier("Expected a table name on the left side of the ON condition.");
        Expect(SqlTokenKind.Dot);
        var leftColumn = ExpectIdentifier("Expected a column name after '.' on the left side of the ON condition.");
        Expect(SqlTokenKind.Equals);
        var rightTable = ExpectIdentifier("Expected a table name on the right side of the ON condition.");
        Expect(SqlTokenKind.Dot);
        var rightColumn = ExpectIdentifier("Expected a column name after '.' on the right side of the ON condition.");

        return new JoinClause(joinType, joinTableName, new JoinCondition(leftTable, leftColumn, rightTable, rightColumn));
    }

    private UpdateStatement ParseUpdate()
    {
        Expect(SqlTokenKind.Update);
        var tableName = ExpectIdentifier("Expected a table name after UPDATE.");
        Expect(SqlTokenKind.Set);

        var assignments = new List<ColumnAssignment>();
        do
        {
            var columnName = ExpectIdentifier("Expected a column name in the UPDATE assignment list.");
            Expect(SqlTokenKind.Equals);
            var value = ParseLiteral();
            assignments.Add(new ColumnAssignment(columnName, value));
        }
        while (Match(SqlTokenKind.Comma));

        Expect(SqlTokenKind.Where);
        var filter = ParseWhereExpression();

        return new UpdateStatement(tableName, assignments, filter);
    }

    private DeleteStatement ParseDelete()
    {
        Expect(SqlTokenKind.Delete);
        Expect(SqlTokenKind.From);
        var tableName = ExpectIdentifier("Expected a table name after DELETE FROM.");

        Expect(SqlTokenKind.Where);
        var filter = ParseWhereExpression();

        return new DeleteStatement(tableName, filter);
    }
    private WhereExpression ParseWhereExpression() => ParseOrExpression();

    private WhereExpression ParseOrExpression()
    {
        var left = ParseAndExpression();
        while (Match(SqlTokenKind.Or))
        {
            var right = ParseAndExpression();
            left = new BinaryLogicalExpression(left, LogicalOp.Or, right);
        }
        return left;
    }

    private WhereExpression ParseAndExpression()
    {
        var left = ParseWhereAtom();
        while (Match(SqlTokenKind.And))
        {
            var right = ParseWhereAtom();
            left = new BinaryLogicalExpression(left, LogicalOp.And, right);
        }
        return left;
    }

    private WhereExpression ParseWhereAtom()
    {
        if (Match(SqlTokenKind.OpenParen))
        {
            var inner = ParseOrExpression();
            Expect(SqlTokenKind.CloseParen);
            return inner;
        }

        string? tableName = null;
        var columnName = ExpectIdentifier("Expected a column name in the WHERE condition.");
        if (Match(SqlTokenKind.Dot))
        {
            tableName = columnName;
            columnName = ExpectIdentifier("Expected a column name after '.' in the WHERE condition.");
        }

        var op = ParseComparisonOp();
        var value = ParseLiteral();
        return new ComparisonExpression(tableName, columnName, op, value);
    }

    private ComparisonOp ParseComparisonOp()
    {
        var token = Advance();
        return token.Kind switch
        {
            SqlTokenKind.Equals => ComparisonOp.Equals,
            SqlTokenKind.NotEquals => ComparisonOp.NotEquals,
            SqlTokenKind.LessThan => ComparisonOp.LessThan,
            SqlTokenKind.GreaterThan => ComparisonOp.GreaterThan,
            SqlTokenKind.LessThanOrEquals => ComparisonOp.LessThanOrEquals,
            SqlTokenKind.GreaterThanOrEquals => ComparisonOp.GreaterThanOrEquals,
            _ => throw new InvalidOperationException($"Expected a comparison operator, but found '{token.Lexeme}'.")
        };
    }

    private ColumnReference ParseColumnReference(string errorMessage)
    {
        var firstName = ExpectIdentifier(errorMessage);
        if (Match(SqlTokenKind.Dot))
        {
            var secondName = ExpectIdentifier("Expected a column name after '.' in the column reference.");
            return new ColumnReference(firstName, secondName);
        }
        return new ColumnReference(null, firstName);
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

internal sealed record SelectStatement(string TableName, IReadOnlyList<ColumnReference>? Columns, WhereExpression? Filter, IReadOnlyList<JoinClause> Joins) : SqlStatement;

internal sealed record DeleteStatement(string TableName, WhereExpression Filter) : SqlStatement;

internal sealed record UpdateStatement(string TableName, IReadOnlyList<ColumnAssignment> Assignments, WhereExpression Filter) : SqlStatement;

internal sealed record ColumnAssignment(string ColumnName, SqlLiteral Value);

internal sealed record ColumnSpecification(string Name, string TypeName, bool IsPrimaryKey);

internal sealed record ColumnReference(string? TableName, string ColumnName);

// JOIN AST
internal enum JoinType { Inner, Left }

internal sealed record JoinCondition(string LeftTable, string LeftColumn, string RightTable, string RightColumn);

internal sealed record JoinClause(JoinType JoinType, string TableName, JoinCondition Condition);

// WHERE expression tree
internal abstract record WhereExpression;

internal sealed record ComparisonExpression(string? TableName, string ColumnName, ComparisonOp Op, SqlLiteral Value) : WhereExpression;

internal sealed record BinaryLogicalExpression(WhereExpression Left, LogicalOp Op, WhereExpression Right) : WhereExpression;

internal enum ComparisonOp { Equals, NotEquals, LessThan, GreaterThan, LessThanOrEquals, GreaterThanOrEquals }

internal enum LogicalOp { And, Or }

internal sealed record SqlLiteral(object Value);
