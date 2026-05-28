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
            SqlTokenKind.Create => ParseCreate(),
            SqlTokenKind.Insert => ParseInsert(),
            SqlTokenKind.Select => ParseSelect(),
            SqlTokenKind.Update => ParseUpdate(),
            SqlTokenKind.Delete => ParseDelete(),
            SqlTokenKind.Begin => ParseBegin(),
            SqlTokenKind.Commit => ParseCommit(),
            SqlTokenKind.Rollback => ParseRollback(),
            SqlTokenKind.Drop => ParseDrop(),
            SqlTokenKind.Alter => ParseAlter(),
            _ => throw new InvalidOperationException($"Unsupported SQL statement starting with token '{Peek().Lexeme}'.")
        };

        Match(SqlTokenKind.Semicolon);
        Expect(SqlTokenKind.EndOfInput);
        return statement;
    }

    /// <summary>Parses all statements in a multi-statement SQL string.</summary>
    public IReadOnlyList<SqlStatement> ParseAll()
    {
        var statements = new List<SqlStatement>();

        while (Peek().Kind != SqlTokenKind.EndOfInput)
        {
            SqlStatement statement = Peek().Kind switch
            {
                SqlTokenKind.Create => ParseCreate(),
                SqlTokenKind.Insert => ParseInsert(),
                SqlTokenKind.Select => ParseSelect(),
                SqlTokenKind.Update => ParseUpdate(),
                SqlTokenKind.Delete => ParseDelete(),
                SqlTokenKind.Begin => ParseBegin(),
                SqlTokenKind.Commit => ParseCommit(),
                SqlTokenKind.Rollback => ParseRollback(),
                SqlTokenKind.Drop => ParseDrop(),
                SqlTokenKind.Alter => ParseAlter(),
                _ => throw new InvalidOperationException($"Unsupported SQL statement starting with token '{Peek().Lexeme}'.")
            };

            Match(SqlTokenKind.Semicolon);
            statements.Add(statement);
        }

        return statements;
    }

    private BeginStatement ParseBegin()
    {
        Expect(SqlTokenKind.Begin);
        // Accept optional TRANSACTION keyword: BEGIN TRANSACTION;
        Match(SqlTokenKind.Transaction);
        return new BeginStatement();
    }

    private CommitStatement ParseCommit()
    {
        Expect(SqlTokenKind.Commit);
        return new CommitStatement();
    }

    private RollbackStatement ParseRollback()
    {
        Expect(SqlTokenKind.Rollback);
        return new RollbackStatement();
    }

    private SqlStatement ParseCreate()
    {
        // Dispatch on the token after CREATE: TABLE | [UNIQUE] INDEX
        var next = PeekAhead(1);
        if (next.Kind == SqlTokenKind.Unique || next.Kind == SqlTokenKind.Index)
            return ParseCreateIndex();
        return ParseCreateTable();
    }

    private DropTableStatement ParseDrop()
    {
        Expect(SqlTokenKind.Drop);
        Expect(SqlTokenKind.Table);
        var tableName = ExpectIdentifier("Expected a table name after DROP TABLE.");
        return new DropTableStatement(tableName);
    }

    private SqlStatement ParseAlter()
    {
        Expect(SqlTokenKind.Alter);
        Expect(SqlTokenKind.Table);
        var tableName = ExpectIdentifier("Expected a table name after ALTER TABLE.");

        if (Match(SqlTokenKind.Rename))
        {
            if (Match(SqlTokenKind.Column))
            {
                // ALTER TABLE t RENAME COLUMN old TO new
                var oldColumn = ExpectIdentifier("Expected the old column name after RENAME COLUMN.");
                Expect(SqlTokenKind.To);
                var newColumn = ExpectIdentifier("Expected the new column name after TO.");
                return new AlterTableRenameColumnStatement(tableName, oldColumn, newColumn);
            }

            // ALTER TABLE t RENAME TO new_name
            Expect(SqlTokenKind.To);
            var newName = ExpectIdentifier("Expected the new table name after RENAME TO.");
            return new AlterTableRenameStatement(tableName, newName);
        }

        if (Match(SqlTokenKind.Add))
        {
            Match(SqlTokenKind.Column); // optional keyword
            var columnName = ExpectIdentifier("Expected a column name after ADD COLUMN.");
            var typeName = ExpectIdentifier("Expected a type name after the column name.");
            var isNotNull = false;
            if (Match(SqlTokenKind.Not))
            {
                Expect(SqlTokenKind.Null);
                isNotNull = true;
            }
            return new AlterTableAddColumnStatement(tableName, new ColumnSpecification(columnName, typeName, IsPrimaryKey: false, IsNotNull: isNotNull));
        }

        throw new InvalidOperationException($"Unsupported ALTER TABLE form. Expected RENAME [TO|COLUMN] or ADD [COLUMN] but found '{Peek().Lexeme}'.");
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
            var isNotNull = false;

            if (Match(SqlTokenKind.Primary))
            {
                Expect(SqlTokenKind.Key);
                isPrimaryKey = true;
                isNotNull = true;
            }
            else if (Match(SqlTokenKind.Not))
            {
                Expect(SqlTokenKind.Null);
                isNotNull = true;
            }

            columns.Add(new ColumnSpecification(columnName, typeName, isPrimaryKey, isNotNull));
        }
        while (Match(SqlTokenKind.Comma));

        Expect(SqlTokenKind.CloseParen);
        return new CreateTableStatement(tableName, columns);
    }

    private CreateIndexStatement ParseCreateIndex()
    {
        Expect(SqlTokenKind.Create);
        var isUnique = Match(SqlTokenKind.Unique);
        Expect(SqlTokenKind.Index);
        var indexName = ExpectIdentifier("Expected an index name after CREATE INDEX.");
        Expect(SqlTokenKind.On);
        var tableName = ExpectIdentifier("Expected a table name after ON.");
        Expect(SqlTokenKind.OpenParen);

        var indexColumns = new List<string>();
        do
        {
            indexColumns.Add(ExpectIdentifier("Expected a column name in the index column list."));
        }
        while (Match(SqlTokenKind.Comma));

        Expect(SqlTokenKind.CloseParen);
        return new CreateIndexStatement(indexName, isUnique, tableName, indexColumns);
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

        IReadOnlyList<SelectItem>? columns;
        if (Match(SqlTokenKind.Star))
        {
            columns = null;
        }
        else
        {
            var selectedItems = new List<SelectItem>();
            do
            {
                selectedItems.Add(ParseSelectItem());
            }
            while (Match(SqlTokenKind.Comma));

            columns = selectedItems;
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

        IReadOnlyList<string>? groupBy = null;
        if (Match(SqlTokenKind.Group))
        {
            Expect(SqlTokenKind.By);
            var groupCols = new List<string>();
            do
            {
                groupCols.Add(ExpectIdentifier("Expected a column name in GROUP BY."));
            }
            while (Match(SqlTokenKind.Comma));
            groupBy = groupCols;
        }

        HavingExpression? having = null;
        if (Match(SqlTokenKind.Having))
        {
            having = ParseHavingExpression();
        }

        IReadOnlyList<OrderByTerm>? orderBy = null;
        if (Match(SqlTokenKind.Order))
        {
            Expect(SqlTokenKind.By);
            var terms = new List<OrderByTerm>();
            do
            {
                var col = ParseColumnReference("Expected a column name in ORDER BY.");
                var descending = Match(SqlTokenKind.Desc);
                if (!descending) Match(SqlTokenKind.Asc); // ASC is the default; consume if present
                terms.Add(new OrderByTerm(col, descending));
            }
            while (Match(SqlTokenKind.Comma));
            orderBy = terms;
        }

        int? limit = null;
        if (Match(SqlTokenKind.Limit))
        {
            var token = Advance();
            if (token.Kind != SqlTokenKind.IntegerLiteral)
                throw new InvalidOperationException("Expected an integer literal after LIMIT.");
            limit = (int)long.Parse(token.Lexeme, System.Globalization.CultureInfo.InvariantCulture);
        }

        int? offset = null;
        if (Match(SqlTokenKind.Offset))
        {
            var token = Advance();
            if (token.Kind != SqlTokenKind.IntegerLiteral)
                throw new InvalidOperationException("Expected an integer literal after OFFSET.");
            offset = (int)long.Parse(token.Lexeme, System.Globalization.CultureInfo.InvariantCulture);
        }

        return new SelectStatement(tableName, columns, filter, joins, groupBy, having, orderBy, limit, offset);
    }

    private SelectItem ParseSelectItem()
    {
        var kind = Peek().Kind;
        if (kind is SqlTokenKind.Count or SqlTokenKind.Sum or SqlTokenKind.Min or SqlTokenKind.Max or SqlTokenKind.Avg)
        {
            return ParseAggregateSelectItem();
        }

        return new ColumnSelectItem(ParseColumnReference("Expected a column name in the SELECT list."));
    }

    private AggregateSelectItem ParseAggregateSelectItem()
    {
        var fnToken = Advance();
        var fn = fnToken.Kind switch
        {
            SqlTokenKind.Count => AggregateFn.Count,
            SqlTokenKind.Sum => AggregateFn.Sum,
            SqlTokenKind.Min => AggregateFn.Min,
            SqlTokenKind.Max => AggregateFn.Max,
            SqlTokenKind.Avg => AggregateFn.Avg,
            _ => throw new InvalidOperationException($"Expected an aggregate function name, but found '{fnToken.Lexeme}'.")
        };

        Expect(SqlTokenKind.OpenParen);

        ColumnReference? argument = null;
        if (fn == AggregateFn.Count && Match(SqlTokenKind.Star))
        {
            // COUNT(*) — argument stays null
        }
        else
        {
            argument = ParseColumnReference($"Expected a column reference inside {fnToken.Lexeme}(...).");
        }

        Expect(SqlTokenKind.CloseParen);
        return new AggregateSelectItem(fn, argument);
    }

    private HavingExpression ParseHavingExpression()
    {
        var fnToken = Advance();
        var fn = fnToken.Kind switch
        {
            SqlTokenKind.Count => AggregateFn.Count,
            SqlTokenKind.Sum => AggregateFn.Sum,
            SqlTokenKind.Min => AggregateFn.Min,
            SqlTokenKind.Max => AggregateFn.Max,
            SqlTokenKind.Avg => AggregateFn.Avg,
            _ => throw new InvalidOperationException($"Expected an aggregate function in HAVING, but found '{fnToken.Lexeme}'.")
        };

        Expect(SqlTokenKind.OpenParen);

        ColumnReference? argument = null;
        if (fn == AggregateFn.Count && Match(SqlTokenKind.Star))
        {
            // COUNT(*) — argument stays null
        }
        else
        {
            argument = ParseColumnReference($"Expected a column reference inside {fnToken.Lexeme}(...).");
        }

        Expect(SqlTokenKind.CloseParen);

        var op = ParseComparisonOp();
        var value = ParseLiteral();
        return new HavingExpression(fn, argument, op, value);
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

        if (Match(SqlTokenKind.Is))
        {
            if (Match(SqlTokenKind.Not))
            {
                Expect(SqlTokenKind.Null);
                return new NullCheckExpression(tableName, columnName, ExpectNull: false);
            }

            Expect(SqlTokenKind.Null);
            return new NullCheckExpression(tableName, columnName, ExpectNull: true);
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
            SqlTokenKind.FloatLiteral => new SqlLiteral((double)(token.Value ?? 0.0)),
            SqlTokenKind.StringLiteral => new SqlLiteral(token.Value ?? string.Empty),
            SqlTokenKind.BlobLiteral => new SqlLiteral((byte[])(token.Value ?? Array.Empty<byte>())),
            SqlTokenKind.True => new SqlLiteral(true),
            SqlTokenKind.False => new SqlLiteral(false),
            SqlTokenKind.Null => new SqlLiteral(null),
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

    private SqlToken PeekAhead(int offset) =>
        _position + offset < _tokens.Count ? _tokens[_position + offset] : _tokens[^1];

    private SqlToken Advance()
    {
        var token = Peek();
        _position++;
        return token;
    }
}

internal abstract record SqlStatement;

internal sealed record CreateTableStatement(string TableName, IReadOnlyList<ColumnSpecification> Columns) : SqlStatement;

internal sealed record CreateIndexStatement(string IndexName, bool IsUnique, string TableName, IReadOnlyList<string> Columns) : SqlStatement;

internal sealed record InsertStatement(string TableName, IReadOnlyList<string>? Columns, IReadOnlyList<SqlLiteral> Values) : SqlStatement;

internal sealed record DropTableStatement(string TableName) : SqlStatement;

internal sealed record AlterTableRenameStatement(string OldName, string NewName) : SqlStatement;

internal sealed record AlterTableAddColumnStatement(string TableName, ColumnSpecification Column) : SqlStatement;

internal sealed record AlterTableRenameColumnStatement(string TableName, string OldColumnName, string NewColumnName) : SqlStatement;

internal sealed record SelectStatement(string TableName, IReadOnlyList<SelectItem>? Columns, WhereExpression? Filter, IReadOnlyList<JoinClause> Joins, IReadOnlyList<string>? GroupBy, HavingExpression? Having, IReadOnlyList<OrderByTerm>? OrderBy, int? Limit, int? Offset) : SqlStatement;

internal sealed record DeleteStatement(string TableName, WhereExpression Filter) : SqlStatement;

internal sealed record UpdateStatement(string TableName, IReadOnlyList<ColumnAssignment> Assignments, WhereExpression Filter) : SqlStatement;

internal sealed record ColumnAssignment(string ColumnName, SqlLiteral Value);

internal sealed record ColumnSpecification(string Name, string TypeName, bool IsPrimaryKey, bool IsNotNull = false);

internal sealed record ColumnReference(string? TableName, string ColumnName);

// JOIN AST
internal enum JoinType { Inner, Left }

internal sealed record JoinCondition(string LeftTable, string LeftColumn, string RightTable, string RightColumn);

internal sealed record JoinClause(JoinType JoinType, string TableName, JoinCondition Condition);

// WHERE expression tree
internal abstract record WhereExpression;

internal sealed record ComparisonExpression(string? TableName, string ColumnName, ComparisonOp Op, SqlLiteral Value) : WhereExpression;

internal sealed record BinaryLogicalExpression(WhereExpression Left, LogicalOp Op, WhereExpression Right) : WhereExpression;

internal sealed record NullCheckExpression(string? TableName, string ColumnName, bool ExpectNull) : WhereExpression;

internal enum ComparisonOp { Equals, NotEquals, LessThan, GreaterThan, LessThanOrEquals, GreaterThanOrEquals }

internal enum LogicalOp { And, Or }

internal sealed record SqlLiteral(object? Value);

// ── SELECT item hierarchy ─────────────────────────────────────────────────────

internal abstract record SelectItem;

internal sealed record ColumnSelectItem(ColumnReference Column) : SelectItem;

/// <summary>Aggregate function call in a SELECT list. <c>Argument</c> is null for COUNT(*).</summary>
internal sealed record AggregateSelectItem(AggregateFn Fn, ColumnReference? Argument) : SelectItem;

internal enum AggregateFn { Count, Sum, Min, Max, Avg }

// ── ORDER BY ─────────────────────────────────────────────────────────────────

internal sealed record OrderByTerm(ColumnReference Column, bool Descending);

// ── HAVING ────────────────────────────────────────────────────────────────────

/// <summary>Single aggregate-comparison predicate: e.g. COUNT(*) &gt; 5.</summary>
internal sealed record HavingExpression(AggregateFn Fn, ColumnReference? Argument, ComparisonOp Op, SqlLiteral Value);
