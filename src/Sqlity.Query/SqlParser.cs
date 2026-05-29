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
            SqlTokenKind.Select => ParseSelectOrSetOperation(),
            SqlTokenKind.Update => ParseUpdate(),
            SqlTokenKind.Delete => ParseDelete(),
            SqlTokenKind.Begin => ParseBegin(),
            SqlTokenKind.Commit => ParseCommit(),
            SqlTokenKind.Rollback => ParseRollback(),
            SqlTokenKind.Drop => ParseDrop(),
            SqlTokenKind.Alter => ParseAlter(),
            SqlTokenKind.Truncate => ParseTruncate(),
            SqlTokenKind.With => ParseCteStatement(),
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
                SqlTokenKind.Select => ParseSelectOrSetOperation(),
                SqlTokenKind.Update => ParseUpdate(),
                SqlTokenKind.Delete => ParseDelete(),
                SqlTokenKind.Begin => ParseBegin(),
                SqlTokenKind.Commit => ParseCommit(),
                SqlTokenKind.Rollback => ParseRollback(),
                SqlTokenKind.Drop => ParseDrop(),
                SqlTokenKind.Alter => ParseAlter(),
                SqlTokenKind.Truncate => ParseTruncate(),
                SqlTokenKind.With => ParseCteStatement(),
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
        // Dispatch on the token after CREATE: TABLE | [UNIQUE] INDEX | VIEW
        var next = PeekAhead(1);
        if (next.Kind == SqlTokenKind.Unique || next.Kind == SqlTokenKind.Index)
            return ParseCreateIndex();
        if (next.Kind == SqlTokenKind.View)
            return ParseCreateView();
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
            var isUnique = false;
            var isAutoIncrement = false;
            SqlLiteral? defaultValue = null;

            // Parse any combination of NOT NULL, NULL, PRIMARY KEY, UNIQUE, AUTOINCREMENT, DEFAULT in any order
            bool consumed;
            do
            {
                consumed = false;
                if (Match(SqlTokenKind.Primary))
                {
                    Expect(SqlTokenKind.Key);
                    isPrimaryKey = true;
                    isNotNull = true;
                    consumed = true;
                }
                else if (Match(SqlTokenKind.Not))
                {
                    Expect(SqlTokenKind.Null);
                    isNotNull = true;
                    consumed = true;
                }
                else if (Match(SqlTokenKind.Null))
                {
                    // Explicit NULL annotation — column is nullable (default), nothing to set
                    consumed = true;
                }
                else if (Match(SqlTokenKind.Unique))
                {
                    isUnique = true;
                    consumed = true;
                }
                else if (Match(SqlTokenKind.Autoincrement) || Match(SqlTokenKind.Serial))
                {
                    isAutoIncrement = true;
                    consumed = true;
                }
                else if (Match(SqlTokenKind.Default))
                {
                    defaultValue = ParseLiteral();
                    consumed = true;
                }
            }
            while (consumed);

            columns.Add(new ColumnSpecification(columnName, typeName, isPrimaryKey, isNotNull, isUnique, isAutoIncrement, defaultValue));
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

    private TruncateTableStatement ParseTruncate()
    {
        Expect(SqlTokenKind.Truncate);
        Match(SqlTokenKind.Table); // TABLE keyword is optional
        var tableName = ExpectIdentifier("Expected a table name after TRUNCATE.");
        return new TruncateTableStatement(tableName);
    }

    private CreateViewStatement ParseCreateView()
    {
        Expect(SqlTokenKind.Create);
        Expect(SqlTokenKind.View);
        var viewName = ExpectIdentifier("Expected a view name after CREATE VIEW.");
        Expect(SqlTokenKind.As);

        // Capture the full SELECT SQL by re-reading from the current position.
        // We parse the SELECT AST to validate syntax, then store the remaining source SQL.
        var selectStartPos = _position;
        ParseSelect(); // validate syntax
        // Reconstruct SQL from the tokens consumed between selectStartPos and _position.
        var selectSql = ReconstructSql(selectStartPos, _position);
        return new CreateViewStatement(viewName, selectSql);
    }

    /// <summary>Reconstructs the SQL source string for a token range by joining lexemes.</summary>
    private string ReconstructSql(int fromToken, int toToken)
    {
        var parts = new System.Text.StringBuilder();
        for (var i = fromToken; i < toToken; i++)
        {
            if (i > fromToken) parts.Append(' ');
            var token = _tokens[i];
            if (token.Kind == SqlTokenKind.StringLiteral)
                parts.Append('\'').Append(token.Lexeme.Replace("'", "''")).Append('\'');
            else
                parts.Append(token.Lexeme);
        }
        return parts.ToString();
    }

    private InsertStatement ParseInsert()
    {
        Expect(SqlTokenKind.Insert);

        // Support INSERT OR REPLACE INTO ...
        var isOrReplace = false;
        if (Match(SqlTokenKind.Or))
        {
            Expect(SqlTokenKind.Replace);
            isOrReplace = true;
        }

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

        // INSERT INTO t SELECT ... — pipe a query result into an insert loop
        if (Peek().Kind == SqlTokenKind.Select)
        {
            var sourceQuery = ParseSelect();
            return new InsertStatement(tableName, columns, ValueRows: null, isOrReplace, (SelectStatement)sourceQuery);
        }

        Expect(SqlTokenKind.Values);

        var valueRows = new List<IReadOnlyList<SqlLiteral>>();
        do
        {
            Expect(SqlTokenKind.OpenParen);
            var values = new List<SqlLiteral>();
            do { values.Add(ParseLiteral()); }
            while (Match(SqlTokenKind.Comma));
            Expect(SqlTokenKind.CloseParen);
            valueRows.Add(values);
        }
        while (Match(SqlTokenKind.Comma));

        return new InsertStatement(tableName, columns, valueRows, isOrReplace);
    }

    /// <summary>
    /// Parses a SELECT statement, then wraps it in a <see cref="SetOperationStatement"/> if
    /// UNION / UNION ALL / INTERSECT / EXCEPT follows (left-associative). ORDER BY / LIMIT / OFFSET
    /// after the last operand apply to the whole set expression.
    /// </summary>
    private SqlStatement ParseSelectOrSetOperation()
    {
        // Parse the left-hand SELECT without consuming trailing ORDER BY/LIMIT/OFFSET
        // because those might belong to the set operation, not to this individual SELECT.
        SqlStatement left = ParseSelect(suppressTrailingClauses: false);

        while (Peek().Kind is SqlTokenKind.Union or SqlTokenKind.Intersect or SqlTokenKind.Except)
        {
            SetOp op;
            if (Peek().Kind == SqlTokenKind.Union)
            {
                Advance(); // consume UNION
                op = Match(SqlTokenKind.All) ? SetOp.UnionAll : SetOp.UnionDistinct;
            }
            else if (Peek().Kind == SqlTokenKind.Intersect)
            {
                Advance();
                op = SetOp.Intersect;
            }
            else
            {
                Advance(); // EXCEPT
                op = SetOp.Except;
            }

            // Right-hand SELECT must NOT consume ORDER BY/LIMIT/OFFSET — those belong to the set op.
            SelectStatement right = ParseSelect(suppressTrailingClauses: true);
            left = new SetOperationStatement(left, op, right, null, null, null);
        }

        if (left is not SetOperationStatement)
            return left;

        // Trailing ORDER BY / LIMIT / OFFSET belong to the set operation result.
        IReadOnlyList<OrderByTerm>? orderBy = null;
        if (Match(SqlTokenKind.Order))
        {
            Expect(SqlTokenKind.By);
            var terms = new List<OrderByTerm>();
            do
            {
                var col = ParseColumnReference("Expected a column name in ORDER BY.");
                var desc = Match(SqlTokenKind.Desc);
                if (!desc) Match(SqlTokenKind.Asc);
                terms.Add(new OrderByTerm(col, desc));
            }
            while (Match(SqlTokenKind.Comma));
            orderBy = terms;
        }

        int? limit = null;
        if (Match(SqlTokenKind.Limit))
        {
            var tok = Advance();
            if (tok.Kind != SqlTokenKind.IntegerLiteral)
                throw new InvalidOperationException("Expected an integer literal after LIMIT.");
            var limitVal = long.Parse(tok.Lexeme, System.Globalization.CultureInfo.InvariantCulture);
            if (limitVal < 0 || limitVal > int.MaxValue)
                throw new InvalidOperationException($"LIMIT value {limitVal} is out of the valid range [0, {int.MaxValue}].");
            limit = (int)limitVal;
        }

        int? offset = null;
        if (Match(SqlTokenKind.Offset))
        {
            var tok = Advance();
            if (tok.Kind != SqlTokenKind.IntegerLiteral)
                throw new InvalidOperationException("Expected an integer literal after OFFSET.");
            var offsetVal = long.Parse(tok.Lexeme, System.Globalization.CultureInfo.InvariantCulture);
            if (offsetVal < 0 || offsetVal > int.MaxValue)
                throw new InvalidOperationException($"OFFSET value {offsetVal} is out of the valid range [0, {int.MaxValue}].");
            offset = (int)offsetVal;
        }

        var setOp = (SetOperationStatement)left;
        return setOp with { OrderBy = orderBy, Limit = limit, Offset = offset };
    }

    /// <summary>Parses WITH name AS (SELECT …) [, …] body.</summary>
    private CteStatement ParseCteStatement()
    {
        Expect(SqlTokenKind.With);
        var ctes = new List<CteDef>();

        do
        {
            var name = ExpectIdentifier("Expected a CTE name after WITH.");
            Expect(SqlTokenKind.As);
            Expect(SqlTokenKind.OpenParen);
            var query = ParseSelect();
            Expect(SqlTokenKind.CloseParen);
            ctes.Add(new CteDef(name, query));
        }
        while (Match(SqlTokenKind.Comma));

        // The body can itself be a set operation.
        SqlStatement body = Peek().Kind == SqlTokenKind.Select
            ? ParseSelectOrSetOperation()
            : throw new InvalidOperationException("Expected SELECT after WITH … AS (…).");

        return new CteStatement(ctes, body);
    }

    private SelectStatement ParseSelect(bool suppressTrailingClauses = false)
    {
        Expect(SqlTokenKind.Select);

        var isDistinct = Match(SqlTokenKind.Distinct);

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

        // Collect alias → real-table-name mappings so that EF-style aliases (AS t0) can be resolved.
        var aliasMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (Match(SqlTokenKind.As))
        {
            var alias = ExpectIdentifier("Expected an alias after AS.");
            aliasMap[alias] = tableName;
        }

        var joins = new List<JoinClause>();
        while (Peek().Kind is SqlTokenKind.Inner or SqlTokenKind.Left or SqlTokenKind.Join)
        {
            joins.Add(ParseJoinClause(aliasMap));
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
        int? limit = null;
        int? offset = null;

        if (!suppressTrailingClauses)
        {
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

            if (Match(SqlTokenKind.Limit))
            {
                var token = Advance();
                if (token.Kind != SqlTokenKind.IntegerLiteral)
                    throw new InvalidOperationException("Expected an integer literal after LIMIT.");
                var limitVal = long.Parse(token.Lexeme, System.Globalization.CultureInfo.InvariantCulture);
                if (limitVal < 0 || limitVal > int.MaxValue)
                    throw new InvalidOperationException($"LIMIT value {limitVal} is out of the valid range [0, {int.MaxValue}].");
                limit = (int)limitVal;
            }

            if (Match(SqlTokenKind.Offset))
            {
                var token = Advance();
                if (token.Kind != SqlTokenKind.IntegerLiteral)
                    throw new InvalidOperationException("Expected an integer literal after OFFSET.");
                var offsetVal = long.Parse(token.Lexeme, System.Globalization.CultureInfo.InvariantCulture);
                if (offsetVal < 0 || offsetVal > int.MaxValue)
                    throw new InvalidOperationException($"OFFSET value {offsetVal} is out of the valid range [0, {int.MaxValue}].");
                offset = (int)offsetVal;
            }
        }

        var stmt = new SelectStatement(tableName, columns, filter, joins, groupBy, having, orderBy, limit, offset, isDistinct);
        return aliasMap.Count > 0 ? ResolveAliases(stmt, aliasMap) : stmt;
    }

    private SelectItem ParseSelectItem()
    {
        var kind = Peek().Kind;
        SelectItem item;

        if (kind is SqlTokenKind.Count or SqlTokenKind.Sum or SqlTokenKind.Min or SqlTokenKind.Max or SqlTokenKind.Avg)
            item = ParseAggregateSelectItem();
        else if (kind == SqlTokenKind.Case)
            item = ParseCaseWhenSelectItem();
        else if (kind is SqlTokenKind.Coalesce or SqlTokenKind.Nullif or SqlTokenKind.Ifnull
                      or SqlTokenKind.Upper or SqlTokenKind.Lower or SqlTokenKind.Trim
                      or SqlTokenKind.Length or SqlTokenKind.Substr
                      or SqlTokenKind.Abs or SqlTokenKind.Round or SqlTokenKind.Ceil or SqlTokenKind.Floor)
            item = ParseScalarFunctionSelectItem();
        else if (kind == SqlTokenKind.Replace && PeekAhead(1).Kind == SqlTokenKind.OpenParen)
            item = ParseScalarFunctionSelectItem();
        else if (kind is SqlTokenKind.RowNumber or SqlTokenKind.Rank or SqlTokenKind.DenseRank
                      or SqlTokenKind.Lag or SqlTokenKind.Lead)
            item = ParseWindowFunctionSelectItem();
        else
            item = new ColumnSelectItem(ParseColumnReference("Expected a column name in the SELECT list."));

        if (Match(SqlTokenKind.As))
        {
            var alias = ExpectIdentifier("Expected an alias after AS in the SELECT list.");
            item = item switch
            {
                ColumnSelectItem col => col with { Alias = alias },
                AggregateSelectItem agg => agg with { Alias = alias },
                ScalarFunctionSelectItem fn => fn with { Alias = alias },
                CaseWhenSelectItem cw => cw with { Alias = alias },
                WindowFunctionSelectItem wf => wf with { Alias = alias },
                _ => item
            };
        }

        return item;
    }

    private WindowFunctionSelectItem ParseWindowFunctionSelectItem()
    {
        var fnToken = Advance();
        var fn = fnToken.Kind switch
        {
            SqlTokenKind.RowNumber => WindowFn.RowNumber,
            SqlTokenKind.Rank => WindowFn.Rank,
            SqlTokenKind.DenseRank => WindowFn.DenseRank,
            SqlTokenKind.Lag => WindowFn.Lag,
            SqlTokenKind.Lead => WindowFn.Lead,
            _ => throw new InvalidOperationException($"Unknown window function token '{fnToken.Lexeme}'.")
        };

        Expect(SqlTokenKind.OpenParen);
        var args = new List<ScalarExpr>();
        if (Peek().Kind != SqlTokenKind.CloseParen)
        {
            do
            {
                args.Add(ParseScalarExpr());
            }
            while (Match(SqlTokenKind.Comma));
        }
        Expect(SqlTokenKind.CloseParen);

        Expect(SqlTokenKind.Over);
        Expect(SqlTokenKind.OpenParen);

        // PARTITION BY
        var partitionBy = new List<ColumnReference>();
        if (Match(SqlTokenKind.Partition))
        {
            Expect(SqlTokenKind.By);
            do
            {
                partitionBy.Add(ParseColumnReference("Expected a column name in PARTITION BY."));
            }
            while (Match(SqlTokenKind.Comma));
        }

        // ORDER BY
        var orderBy = new List<OrderByTerm>();
        if (Match(SqlTokenKind.Order))
        {
            Expect(SqlTokenKind.By);
            do
            {
                var col = ParseColumnReference("Expected a column name in window ORDER BY.");
                var desc = Match(SqlTokenKind.Desc);
                if (!desc) Match(SqlTokenKind.Asc);
                orderBy.Add(new OrderByTerm(col, desc));
            }
            while (Match(SqlTokenKind.Comma));
        }

        Expect(SqlTokenKind.CloseParen);

        return new WindowFunctionSelectItem(fn, args, new WindowSpec(partitionBy, orderBy));
    }

    private ScalarFunctionSelectItem ParseScalarFunctionSelectItem()
    {
        var fnToken = Advance();
        var fn = fnToken.Kind switch
        {
            SqlTokenKind.Coalesce => ScalarFn.Coalesce,
            SqlTokenKind.Nullif => ScalarFn.Nullif,
            SqlTokenKind.Ifnull => ScalarFn.Ifnull,
            SqlTokenKind.Upper => ScalarFn.Upper,
            SqlTokenKind.Lower => ScalarFn.Lower,
            SqlTokenKind.Trim => ScalarFn.Trim,
            SqlTokenKind.Length => ScalarFn.Length,
            SqlTokenKind.Substr => ScalarFn.Substr,
            SqlTokenKind.Replace => ScalarFn.Replace,
            SqlTokenKind.Abs => ScalarFn.Abs,
            SqlTokenKind.Round => ScalarFn.Round,
            SqlTokenKind.Ceil => ScalarFn.Ceil,
            SqlTokenKind.Floor => ScalarFn.Floor,
            _ => throw new InvalidOperationException($"Expected a scalar function name, but found '{fnToken.Lexeme}'.")
        };

        Expect(SqlTokenKind.OpenParen);
        var args = new List<ScalarExpr>();
        do { args.Add(ParseScalarExpr()); }
        while (Match(SqlTokenKind.Comma));
        Expect(SqlTokenKind.CloseParen);

        if (fn is ScalarFn.Nullif or ScalarFn.Ifnull && args.Count != 2)
            throw new InvalidOperationException($"{fnToken.Lexeme} requires exactly 2 arguments.");
        if (fn == ScalarFn.Coalesce && args.Count < 2)
            throw new InvalidOperationException("COALESCE requires at least 2 arguments.");
        if (fn is ScalarFn.Upper or ScalarFn.Lower or ScalarFn.Trim or ScalarFn.Length
                 or ScalarFn.Abs or ScalarFn.Ceil or ScalarFn.Floor && args.Count != 1)
            throw new InvalidOperationException($"{fnToken.Lexeme} requires exactly 1 argument.");
        if (fn == ScalarFn.Substr && args.Count is not (2 or 3))
            throw new InvalidOperationException("SUBSTR requires 2 or 3 arguments.");
        if (fn == ScalarFn.Replace && args.Count != 3)
            throw new InvalidOperationException("REPLACE requires exactly 3 arguments.");
        if (fn == ScalarFn.Round && args.Count is not (1 or 2))
            throw new InvalidOperationException("ROUND requires 1 or 2 arguments.");

        return new ScalarFunctionSelectItem(fn, args);
    }

    private ScalarExpr ParseScalarExpr()
    {
        var kind = Peek().Kind;
        if (kind is SqlTokenKind.StringLiteral or SqlTokenKind.IntegerLiteral or SqlTokenKind.FloatLiteral
            or SqlTokenKind.BlobLiteral or SqlTokenKind.True or SqlTokenKind.False or SqlTokenKind.Null)
            return new LiteralScalarExpr(ParseLiteral());

        return new ColumnScalarExpr(ParseColumnReference("Expected a column reference or literal in scalar function arguments."));
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

    private JoinClause ParseJoinClause(Dictionary<string, string>? aliasMap = null)
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

        if (aliasMap != null && Match(SqlTokenKind.As))
        {
            var alias = ExpectIdentifier("Expected an alias after AS.");
            aliasMap[alias] = joinTableName;
        }

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

        WhereExpression? filter = null;
        if (Match(SqlTokenKind.Where))
            filter = ParseWhereExpression();

        return new UpdateStatement(tableName, assignments, filter);
    }

    private DeleteStatement ParseDelete()
    {
        Expect(SqlTokenKind.Delete);
        Expect(SqlTokenKind.From);
        var tableName = ExpectIdentifier("Expected a table name after DELETE FROM.");

        WhereExpression? filter = null;
        if (Match(SqlTokenKind.Where))
            filter = ParseWhereExpression();

        return new DeleteStatement(tableName, filter);
    }

    private CaseWhenSelectItem ParseCaseWhenSelectItem()
    {
        var (branches, elseResult) = ParseCaseWhenBranches();
        return new CaseWhenSelectItem(branches, elseResult);
    }

    private WhereExpression ParseCaseWhenWhereAtom()
    {
        var (branches, elseResult) = ParseCaseWhenBranches();
        var op = ParseComparisonOp();
        var value = ParseLiteral();
        return new CaseWhenWhereExpression(branches, elseResult, op, value);
    }

    private (IReadOnlyList<CaseWhenBranch> Branches, ScalarExpr? ElseResult) ParseCaseWhenBranches()
    {
        Expect(SqlTokenKind.Case);

        var branches = new List<CaseWhenBranch>();
        while (Peek().Kind == SqlTokenKind.When)
        {
            Advance(); // consume WHEN
            var condition = ParseWhereExpression();
            Expect(SqlTokenKind.Then);
            var result = ParseScalarExpr();
            branches.Add(new CaseWhenBranch(condition, result));
        }

        if (branches.Count == 0)
            throw new InvalidOperationException("CASE expression requires at least one WHEN branch.");

        ScalarExpr? elseResult = null;
        if (Match(SqlTokenKind.Else))
            elseResult = ParseScalarExpr();

        Expect(SqlTokenKind.End);
        return (branches, elseResult);
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

        if (Peek().Kind == SqlTokenKind.Case)
            return ParseCaseWhenWhereAtom();

        // EXISTS (SELECT …) / NOT EXISTS (SELECT …)
        if (Peek().Kind == SqlTokenKind.Exists ||
            (Peek().Kind == SqlTokenKind.Not && PeekAhead(1).Kind == SqlTokenKind.Exists))
        {
            bool existsNegated = Match(SqlTokenKind.Not);
            Advance(); // consume EXISTS
            Expect(SqlTokenKind.OpenParen);
            var subquery = ParseSelect();
            Expect(SqlTokenKind.CloseParen);
            return new ExistsExpression(subquery, Negated: existsNegated);
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

        // Handle NOT before IN / BETWEEN / LIKE
        var negated = Match(SqlTokenKind.Not);

        if (Match(SqlTokenKind.In))
        {
            Expect(SqlTokenKind.OpenParen);
            var subquery = ParseSelect();
            Expect(SqlTokenKind.CloseParen);
            return new InSubqueryExpression(tableName, columnName, subquery, Negated: negated);
        }

        if (Match(SqlTokenKind.Between))
        {
            var low = ParseLiteral();
            Expect(SqlTokenKind.And);
            var high = ParseLiteral();
            return new BetweenExpression(tableName, columnName, low, high, Negated: negated);
        }

        if (Peek().Kind is SqlTokenKind.Like or SqlTokenKind.ILike)
        {
            var caseInsensitive = Peek().Kind == SqlTokenKind.ILike;
            Advance();
            var patternToken = Advance();
            if (patternToken.Kind != SqlTokenKind.StringLiteral)
                throw new InvalidOperationException("Expected a string literal after LIKE/ILIKE.");
            return new LikeExpression(tableName, columnName, (string)patternToken.Value!, caseInsensitive, Negated: negated);
        }

        if (negated)
            throw new InvalidOperationException($"NOT must be followed by IN, BETWEEN, or LIKE, but found '{Peek().Lexeme}'.");

        var op = ParseComparisonOp();

        // Scalar subquery: col = (SELECT …)
        if (Peek().Kind == SqlTokenKind.OpenParen && PeekAhead(1).Kind == SqlTokenKind.Select)
        {
            Match(SqlTokenKind.OpenParen);
            var subquery = ParseSelect();
            Expect(SqlTokenKind.CloseParen);
            return new ScalarSubqueryComparisonExpression(tableName, columnName, op, subquery);
        }

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

    // ── Alias resolution ─────────────────────────────────────────────────────────
    // EF Core generates aliases like `FROM Users AS u0` and then references `u0.Id`.
    // After parsing, we walk the AST and replace alias names with real table names so
    // the existing executor — which resolves column references by table name — works
    // without any changes.

    private static SelectStatement ResolveAliases(SelectStatement stmt, Dictionary<string, string> aliasMap)
    {
        var columns = stmt.Columns?.Select(item => item switch
        {
            ColumnSelectItem c => c with { Column = ResolveRef(c.Column, aliasMap) },
            AggregateSelectItem a => a with { Argument = a.Argument is null ? null : ResolveRef(a.Argument, aliasMap) },
            ScalarFunctionSelectItem f => f with
            {
                Args = f.Args.Select(arg => arg switch
                {
                    ColumnScalarExpr ce => new ColumnScalarExpr(ResolveRef(ce.Column, aliasMap)),
                    _ => arg
                }).ToList()
            },
            CaseWhenSelectItem cw => cw with
            {
                Branches = cw.Branches.Select(b => b with { Condition = ResolveWhereAliases(b.Condition, aliasMap) }).ToList()
            },
            _ => item
        }).ToList<SelectItem>();

        var filter = stmt.Filter is null ? null : ResolveWhereAliases(stmt.Filter, aliasMap);

        var joins = stmt.Joins.Select(j => new JoinClause(j.JoinType, j.TableName, new JoinCondition(
            ResolveTableAlias(j.Condition.LeftTable, aliasMap), j.Condition.LeftColumn,
            ResolveTableAlias(j.Condition.RightTable, aliasMap), j.Condition.RightColumn
        ))).ToList();

        var orderBy = stmt.OrderBy?.Select(t => new OrderByTerm(ResolveRef(t.Column, aliasMap), t.Descending)).ToList();

        return new SelectStatement(stmt.TableName, columns, filter, joins, stmt.GroupBy, stmt.Having, orderBy, stmt.Limit, stmt.Offset, stmt.IsDistinct);
    }

    private static ColumnReference ResolveRef(ColumnReference col, Dictionary<string, string> aliasMap) =>
        col.TableName is null ? col : new ColumnReference(ResolveTableAlias(col.TableName, aliasMap), col.ColumnName);

    private static string ResolveTableAlias(string tableOrAlias, Dictionary<string, string> aliasMap) =>
        aliasMap.TryGetValue(tableOrAlias, out var realName) ? realName : tableOrAlias;

    private static WhereExpression ResolveWhereAliases(WhereExpression expr, Dictionary<string, string> aliasMap) => expr switch
    {
        ComparisonExpression c => c with { TableName = c.TableName is null ? null : ResolveTableAlias(c.TableName, aliasMap) },
        NullCheckExpression n => n with { TableName = n.TableName is null ? null : ResolveTableAlias(n.TableName, aliasMap) },
        BinaryLogicalExpression b => new BinaryLogicalExpression(
            ResolveWhereAliases(b.Left, aliasMap), b.Op, ResolveWhereAliases(b.Right, aliasMap)),
        InSubqueryExpression i => i with { TableName = i.TableName is null ? null : ResolveTableAlias(i.TableName, aliasMap) },
        ScalarSubqueryComparisonExpression s => s with { TableName = s.TableName is null ? null : ResolveTableAlias(s.TableName, aliasMap) },
        LikeExpression l => l with { TableName = l.TableName is null ? null : ResolveTableAlias(l.TableName, aliasMap) },
        BetweenExpression be => be with { TableName = be.TableName is null ? null : ResolveTableAlias(be.TableName, aliasMap) },
        CaseWhenWhereExpression cw => cw with
        {
            Branches = cw.Branches.Select(b => b with { Condition = ResolveWhereAliases(b.Condition, aliasMap) }).ToList()
        },
        _ => expr
    };
}

internal abstract record SqlStatement;

internal sealed record CreateTableStatement(string TableName, IReadOnlyList<ColumnSpecification> Columns) : SqlStatement;

internal sealed record CreateIndexStatement(string IndexName, bool IsUnique, string TableName, IReadOnlyList<string> Columns) : SqlStatement;

internal sealed record InsertStatement(string TableName, IReadOnlyList<string>? Columns, IReadOnlyList<IReadOnlyList<SqlLiteral>>? ValueRows, bool IsOrReplace = false, SelectStatement? SourceQuery = null) : SqlStatement;

internal sealed record DropTableStatement(string TableName) : SqlStatement;

internal sealed record AlterTableRenameStatement(string OldName, string NewName) : SqlStatement;

internal sealed record AlterTableAddColumnStatement(string TableName, ColumnSpecification Column) : SqlStatement;

internal sealed record AlterTableRenameColumnStatement(string TableName, string OldColumnName, string NewColumnName) : SqlStatement;

internal sealed record TruncateTableStatement(string TableName) : SqlStatement;

internal sealed record CreateViewStatement(string ViewName, string SelectSql) : SqlStatement;

internal sealed record SelectStatement(string TableName, IReadOnlyList<SelectItem>? Columns, WhereExpression? Filter, IReadOnlyList<JoinClause> Joins, IReadOnlyList<string>? GroupBy, HavingExpression? Having, IReadOnlyList<OrderByTerm>? OrderBy, int? Limit, int? Offset, bool IsDistinct = false) : SqlStatement;

internal sealed record DeleteStatement(string TableName, WhereExpression? Filter) : SqlStatement;

internal sealed record UpdateStatement(string TableName, IReadOnlyList<ColumnAssignment> Assignments, WhereExpression? Filter) : SqlStatement;

internal sealed record ColumnAssignment(string ColumnName, SqlLiteral Value);

internal sealed record ColumnSpecification(string Name, string TypeName, bool IsPrimaryKey, bool IsNotNull = false, bool IsUnique = false, bool IsAutoIncrement = false, SqlLiteral? DefaultValue = null);

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

/// <summary>col IN (SELECT …) — produced by the parser; resolved to <see cref="InValuesExpression"/> at execution time.</summary>
internal sealed record InSubqueryExpression(string? TableName, string ColumnName, SelectStatement Subquery, bool Negated = false) : WhereExpression;

/// <summary>col = (SELECT …) — produced by the parser; resolved to <see cref="ComparisonExpression"/> at execution time.</summary>
internal sealed record ScalarSubqueryComparisonExpression(string? TableName, string ColumnName, ComparisonOp Op, SelectStatement Subquery) : WhereExpression;

/// <summary>col IN (v1, v2, …) — produced at execution time after pre-evaluating an <see cref="InSubqueryExpression"/>.</summary>
internal sealed record InValuesExpression(string? TableName, string ColumnName, IReadOnlyList<object?> Values, bool Negated = false) : WhereExpression;

internal sealed record LikeExpression(string? TableName, string ColumnName, string Pattern, bool CaseInsensitive, bool Negated = false) : WhereExpression;

internal sealed record BetweenExpression(string? TableName, string ColumnName, SqlLiteral Low, SqlLiteral High, bool Negated = false) : WhereExpression;

/// <summary>EXISTS (SELECT …) / NOT EXISTS (SELECT …) — produced by the parser; resolved to <see cref="BoolLiteralExpression"/> at execution time.</summary>
internal sealed record ExistsExpression(SelectStatement Subquery, bool Negated = false) : WhereExpression;

/// <summary>A constant boolean — produced at execution time after pre-evaluating an <see cref="ExistsExpression"/>.</summary>
internal sealed record BoolLiteralExpression(bool Value) : WhereExpression;

internal enum ComparisonOp { Equals, NotEquals, LessThan, GreaterThan, LessThanOrEquals, GreaterThanOrEquals }

internal enum LogicalOp { And, Or }

internal sealed record SqlLiteral(object? Value);

// ── SELECT item hierarchy ─────────────────────────────────────────────────────

internal abstract record SelectItem;

internal sealed record ColumnSelectItem(ColumnReference Column, string? Alias = null) : SelectItem;

/// <summary>Aggregate function call in a SELECT list. <c>Argument</c> is null for COUNT(*).</summary>
internal sealed record AggregateSelectItem(AggregateFn Fn, ColumnReference? Argument, string? Alias = null) : SelectItem;

internal enum AggregateFn { Count, Sum, Min, Max, Avg }

internal enum ScalarFn { Coalesce, Nullif, Ifnull, Upper, Lower, Trim, Length, Substr, Replace, Abs, Round, Ceil, Floor }

internal abstract record ScalarExpr;

internal sealed record ColumnScalarExpr(ColumnReference Column) : ScalarExpr;

internal sealed record LiteralScalarExpr(SqlLiteral Value) : ScalarExpr;

internal sealed record ScalarFunctionSelectItem(ScalarFn Fn, IReadOnlyList<ScalarExpr> Args, string? Alias = null) : SelectItem;

// ── CASE WHEN ─────────────────────────────────────────────────────────────────

internal sealed record CaseWhenBranch(WhereExpression Condition, ScalarExpr Result);

internal sealed record CaseWhenSelectItem(IReadOnlyList<CaseWhenBranch> Branches, ScalarExpr? ElseResult, string? Alias = null) : SelectItem;

/// <summary>CASE WHEN … END op literal — used in WHERE clauses.</summary>
internal sealed record CaseWhenWhereExpression(IReadOnlyList<CaseWhenBranch> Branches, ScalarExpr? ElseResult, ComparisonOp Op, SqlLiteral Value) : WhereExpression;

// ── ORDER BY ─────────────────────────────────────────────────────────────────

internal sealed record OrderByTerm(ColumnReference Column, bool Descending);

// ── HAVING ────────────────────────────────────────────────────────────────────

/// <summary>Single aggregate-comparison predicate: e.g. COUNT(*) &gt; 5.</summary>
internal sealed record HavingExpression(AggregateFn Fn, ColumnReference? Argument, ComparisonOp Op, SqlLiteral Value);

// ── SET OPERATIONS (UNION / INTERSECT / EXCEPT) ───────────────────────────────

internal enum SetOp { UnionAll, UnionDistinct, Intersect, Except }

/// <summary>Combines two query results with a set operator. Optional ORDER BY / LIMIT / OFFSET apply to the final result.</summary>
internal sealed record SetOperationStatement(
    SqlStatement Left,
    SetOp Op,
    SqlStatement Right,
    IReadOnlyList<OrderByTerm>? OrderBy,
    int? Limit,
    int? Offset) : SqlStatement;

// ── COMMON TABLE EXPRESSIONS ──────────────────────────────────────────────────

internal sealed record CteDef(string Name, SelectStatement Query);

/// <summary>WITH name AS (SELECT …) [, …] body — CTEs are materialised as temp tables before executing the body.</summary>
internal sealed record CteStatement(IReadOnlyList<CteDef> Ctes, SqlStatement Body) : SqlStatement;

// ── WINDOW FUNCTIONS ──────────────────────────────────────────────────────────

internal enum WindowFn { RowNumber, Rank, DenseRank, Lag, Lead }

/// <summary>OVER (PARTITION BY … ORDER BY …) clause attached to a window function.</summary>
internal sealed record WindowSpec(
    IReadOnlyList<ColumnReference> PartitionBy,
    IReadOnlyList<OrderByTerm> OrderBy);

/// <summary>Window function call in a SELECT list: fn([args]) OVER (…).</summary>
internal sealed record WindowFunctionSelectItem(
    WindowFn Fn,
    IReadOnlyList<ScalarExpr> Args,
    WindowSpec Spec,
    string? Alias = null) : SelectItem;
