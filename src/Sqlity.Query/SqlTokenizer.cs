using System.Globalization;

namespace Sqlity.Query;

internal static class SqlTokenizer
{
    public static IReadOnlyList<SqlToken> Tokenize(string sql)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sql);

        var tokens = new List<SqlToken>();
        var index = 0;
        var line = 1;
        var col = 1;

        while (index < sql.Length)
        {
            var current = sql[index];
            if (char.IsWhiteSpace(current))
            {
                AdvanceWhitespace(current, ref index, ref line, ref col);
                continue;
            }

            var tokenLine = line;
            var tokenCol = col;
            var startIndex = index;

            if (IsBlobLiteralStart(sql, index))
            {
                tokens.Add(ReadBlobLiteral(sql, ref index, tokenLine, tokenCol));
                UpdatePosition(sql, startIndex, index, ref line, ref col);
                continue;
            }

            if (char.IsLetter(current) || current == '_')
            {
                tokens.Add(ReadIdentifierOrKeyword(sql, ref index, tokenLine, tokenCol));
                UpdatePosition(sql, startIndex, index, ref line, ref col);
                continue;
            }

            if (char.IsDigit(current) || (current == '-' && index + 1 < sql.Length && char.IsDigit(sql[index + 1])))
            {
                tokens.Add(ReadNumericLiteral(sql, ref index, tokenLine, tokenCol));
                UpdatePosition(sql, startIndex, index, ref line, ref col);
                continue;
            }

            if (current == '\'')
            {
                tokens.Add(ReadStringLiteral(sql, ref index, tokenLine, tokenCol));
                UpdatePosition(sql, startIndex, index, ref line, ref col);
                continue;
            }

            if (current is '<' or '>')
            {
                var next = index + 1 < sql.Length ? sql[index + 1] : '\0';
                if (current == '<' && next == '=') { tokens.Add(new SqlToken(SqlTokenKind.LessThanOrEquals, "<=", null, tokenLine, tokenCol)); index += 2; }
                else if (current == '<' && next == '>') { tokens.Add(new SqlToken(SqlTokenKind.NotEquals, "<>", null, tokenLine, tokenCol)); index += 2; }
                else if (current == '<') { tokens.Add(new SqlToken(SqlTokenKind.LessThan, "<", null, tokenLine, tokenCol)); index++; }
                else if (current == '>' && next == '=') { tokens.Add(new SqlToken(SqlTokenKind.GreaterThanOrEquals, ">=", null, tokenLine, tokenCol)); index += 2; }
                else { tokens.Add(new SqlToken(SqlTokenKind.GreaterThan, ">", null, tokenLine, tokenCol)); index++; }
                UpdatePosition(sql, startIndex, index, ref line, ref col);
                continue;
            }

            if (current == '!')
            {
                var next = index + 1 < sql.Length ? sql[index + 1] : '\0';
                if (next == '=') { tokens.Add(new SqlToken(SqlTokenKind.NotEquals, "!=", null, tokenLine, tokenCol)); index += 2; }
                else { throw new InvalidOperationException($"[{tokenLine}:{tokenCol}] Unexpected SQL character '!'."); }
                UpdatePosition(sql, startIndex, index, ref line, ref col);
                continue;
            }

            tokens.Add(current switch
            {
                '(' => new SqlToken(SqlTokenKind.OpenParen, "(", null, tokenLine, tokenCol),
                ')' => new SqlToken(SqlTokenKind.CloseParen, ")", null, tokenLine, tokenCol),
                ',' => new SqlToken(SqlTokenKind.Comma, ",", null, tokenLine, tokenCol),
                ';' => new SqlToken(SqlTokenKind.Semicolon, ";", null, tokenLine, tokenCol),
                '*' => new SqlToken(SqlTokenKind.Star, "*", null, tokenLine, tokenCol),
                '=' => new SqlToken(SqlTokenKind.Equals, "=", null, tokenLine, tokenCol),
                '.' => new SqlToken(SqlTokenKind.Dot, ".", null, tokenLine, tokenCol),
                _ => throw new InvalidOperationException($"[{tokenLine}:{tokenCol}] Unexpected SQL character '{current}'.")
            });

            index++;
            col++;
        }

        tokens.Add(new SqlToken(SqlTokenKind.EndOfInput, string.Empty, null, line, col));
        return tokens;
    }

    private static void AdvanceWhitespace(char current, ref int index, ref int line, ref int col)
    {
        if (current == '\n') { line++; col = 1; }
        else if (current == '\r') { line++; col = 1; }
        else { col++; }
        index++;
    }

    /// <summary>Advances <paramref name="line"/>/<paramref name="col"/> by scanning [from, to) in <paramref name="sql"/>.</summary>
    private static void UpdatePosition(string sql, int from, int to, ref int line, ref int col)
    {
        for (var i = from; i < to; i++)
        {
            var c = sql[i];
            if (c == '\n') { line++; col = 1; }
            else if (c == '\r')
            {
                line++; col = 1;
                if (i + 1 < to && sql[i + 1] == '\n') i++; // skip the \n of \r\n
            }
            else { col++; }
        }
    }

    private static bool IsBlobLiteralStart(string sql, int index) =>
        index + 1 < sql.Length &&
        (sql[index] == 'x' || sql[index] == 'X') &&
        sql[index + 1] == '\'';

    private static SqlToken ReadIdentifierOrKeyword(string sql, ref int index, int line, int col)
    {
        var start = index;
        index++;

        while (index < sql.Length && (char.IsLetterOrDigit(sql[index]) || sql[index] == '_'))
        {
            index++;
        }

        var lexeme = sql[start..index];
        var kind = lexeme.ToUpperInvariant() switch
        {
            "CREATE" => SqlTokenKind.Create,
            "TABLE" => SqlTokenKind.Table,
            "PRIMARY" => SqlTokenKind.Primary,
            "KEY" => SqlTokenKind.Key,
            "INSERT" => SqlTokenKind.Insert,
            "INTO" => SqlTokenKind.Into,
            "VALUES" => SqlTokenKind.Values,
            "SELECT" => SqlTokenKind.Select,
            "FROM" => SqlTokenKind.From,
            "WHERE" => SqlTokenKind.Where,
            "TRUE" => SqlTokenKind.True,
            "FALSE" => SqlTokenKind.False,
            "UPDATE" => SqlTokenKind.Update,
            "DELETE" => SqlTokenKind.Delete,
            "SET" => SqlTokenKind.Set,
            "AND" => SqlTokenKind.And,
            "OR" => SqlTokenKind.Or,
            "INNER" => SqlTokenKind.Inner,
            "LEFT" => SqlTokenKind.Left,
            "JOIN" => SqlTokenKind.Join,
            "ON" => SqlTokenKind.On,
            "NULL" => SqlTokenKind.Null,
            "IS" => SqlTokenKind.Is,
            "NOT" => SqlTokenKind.Not,
            "BEGIN" => SqlTokenKind.Begin,
            "COMMIT" => SqlTokenKind.Commit,
            "ROLLBACK" => SqlTokenKind.Rollback,
            "TRANSACTION" => SqlTokenKind.Transaction,
            "INDEX" => SqlTokenKind.Index,
            "UNIQUE" => SqlTokenKind.Unique,
            "ORDER" => SqlTokenKind.Order,
            "BY" => SqlTokenKind.By,
            "ASC" => SqlTokenKind.Asc,
            "DESC" => SqlTokenKind.Desc,
            "LIMIT" => SqlTokenKind.Limit,
            "OFFSET" => SqlTokenKind.Offset,
            "GROUP" => SqlTokenKind.Group,
            "HAVING" => SqlTokenKind.Having,
            "COUNT" => SqlTokenKind.Count,
            "SUM" => SqlTokenKind.Sum,
            "MIN" => SqlTokenKind.Min,
            "MAX" => SqlTokenKind.Max,
            "AVG" => SqlTokenKind.Avg,
            "DROP" => SqlTokenKind.Drop,
            "ALTER" => SqlTokenKind.Alter,
            "ADD" => SqlTokenKind.Add,
            "COLUMN" => SqlTokenKind.Column,
            "RENAME" => SqlTokenKind.Rename,
            "TO" => SqlTokenKind.To,
            "IN" => SqlTokenKind.In,
            "AS" => SqlTokenKind.As,
            "LIKE" => SqlTokenKind.Like,
            "ILIKE" => SqlTokenKind.ILike,
            "BETWEEN" => SqlTokenKind.Between,
            "DISTINCT" => SqlTokenKind.Distinct,
            "COALESCE" => SqlTokenKind.Coalesce,
            "NULLIF" => SqlTokenKind.Nullif,
            "IFNULL" => SqlTokenKind.Ifnull,
            "DEFAULT" => SqlTokenKind.Default,
            "AUTOINCREMENT" => SqlTokenKind.Autoincrement,
            "SERIAL" => SqlTokenKind.Serial,
            "REPLACE" => SqlTokenKind.Replace,
            "VIEW" => SqlTokenKind.View,
            "TRUNCATE" => SqlTokenKind.Truncate,
            "UPPER" => SqlTokenKind.Upper,
            "LOWER" => SqlTokenKind.Lower,
            "TRIM" => SqlTokenKind.Trim,
            "LENGTH" => SqlTokenKind.Length,
            "SUBSTR" => SqlTokenKind.Substr,
            "ABS" => SqlTokenKind.Abs,
            "ROUND" => SqlTokenKind.Round,
            "CEIL" => SqlTokenKind.Ceil,
            "FLOOR" => SqlTokenKind.Floor,
            "CASE" => SqlTokenKind.Case,
            "WHEN" => SqlTokenKind.When,
            "THEN" => SqlTokenKind.Then,
            "ELSE" => SqlTokenKind.Else,
            "END" => SqlTokenKind.End,
            "EXISTS" => SqlTokenKind.Exists,
            "UNION" => SqlTokenKind.Union,
            "ALL" => SqlTokenKind.All,
            "INTERSECT" => SqlTokenKind.Intersect,
            "EXCEPT" => SqlTokenKind.Except,
            "WITH" => SqlTokenKind.With,
            "OVER" => SqlTokenKind.Over,
            "PARTITION" => SqlTokenKind.Partition,
            "ROW_NUMBER" => SqlTokenKind.RowNumber,
            "RANK" => SqlTokenKind.Rank,
            "DENSE_RANK" => SqlTokenKind.DenseRank,
            "LAG" => SqlTokenKind.Lag,
            "LEAD" => SqlTokenKind.Lead,
            "ANALYZE" => SqlTokenKind.Analyze,
            "EXPLAIN" => SqlTokenKind.Explain,
            "QUERY" => SqlTokenKind.Query,
            "PLAN" => SqlTokenKind.Plan,
            _ => SqlTokenKind.Identifier
        };

        return new SqlToken(kind, lexeme, null, line, col);
    }

    private static SqlToken ReadNumericLiteral(string sql, ref int index, int line, int col)
    {
        var start = index;
        index++;

        while (index < sql.Length && char.IsDigit(sql[index]))
        {
            index++;
        }

        if (index < sql.Length && sql[index] == '.' && index + 1 < sql.Length && char.IsDigit(sql[index + 1]))
        {
            index++; // consume '.'
            while (index < sql.Length && char.IsDigit(sql[index]))
            {
                index++;
            }

            var floatLexeme = sql[start..index];
            var floatValue = double.Parse(floatLexeme, CultureInfo.InvariantCulture);
            return new SqlToken(SqlTokenKind.FloatLiteral, floatLexeme, floatValue, line, col);
        }

        var lexeme = sql[start..index];
        _ = long.Parse(lexeme, CultureInfo.InvariantCulture);
        return new SqlToken(SqlTokenKind.IntegerLiteral, lexeme, null, line, col);
    }

    private static SqlToken ReadStringLiteral(string sql, ref int index, int line, int col)
    {
        index++;
        var builder = new System.Text.StringBuilder();

        while (index < sql.Length)
        {
            if (sql[index] == '\'')
            {
                if (index + 1 < sql.Length && sql[index + 1] == '\'')
                {
                    builder.Append('\'');
                    index += 2;
                    continue;
                }

                index++;
                return new SqlToken(SqlTokenKind.StringLiteral, builder.ToString(), builder.ToString(), line, col);
            }

            builder.Append(sql[index]);
            index++;
        }

        throw new InvalidOperationException($"[{line}:{col}] Unterminated SQL string literal.");
    }

    private static SqlToken ReadBlobLiteral(string sql, ref int index, int line, int col)
    {
        index += 2;
        var start = index;

        while (index < sql.Length && sql[index] != '\'')
        {
            index++;
        }

        if (index >= sql.Length)
        {
            throw new InvalidOperationException($"[{line}:{col}] Unterminated SQL blob literal.");
        }

        var hex = sql[start..index];
        if (hex.Length % 2 != 0)
        {
            throw new InvalidOperationException($"[{line}:{col}] Blob literals must contain an even number of hex characters.");
        }

        var bytes = new byte[hex.Length / 2];
        for (var i = 0; i < bytes.Length; i++)
        {
            bytes[i] = byte.Parse(hex.AsSpan(i * 2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        }

        index++;
        return new SqlToken(SqlTokenKind.BlobLiteral, $"X'{hex}'", bytes, line, col);
    }
}

internal sealed record SqlToken(SqlTokenKind Kind, string Lexeme, object? Value = null, int Line = 0, int Column = 0);

internal enum SqlTokenKind
{
    Identifier = 0,
    IntegerLiteral = 1,
    StringLiteral = 2,
    BlobLiteral = 3,
    OpenParen = 4,
    CloseParen = 5,
    Comma = 6,
    Semicolon = 7,
    Star = 8,
    Equals = 9,
    Create = 10,
    Table = 11,
    Primary = 12,
    Key = 13,
    Insert = 14,
    Into = 15,
    Values = 16,
    Select = 17,
    From = 18,
    Where = 19,
    True = 20,
    False = 21,
    EndOfInput = 22,
    Update = 23,
    Delete = 24,
    Set = 25,
    And = 26,
    Or = 27,
    LessThan = 28,
    GreaterThan = 29,
    LessThanOrEquals = 30,
    GreaterThanOrEquals = 31,
    NotEquals = 32,
    Inner = 33,
    Left = 34,
    Join = 35,
    On = 36,
    Dot = 37,
    Null = 38,
    Is = 39,
    Not = 40,
    Begin = 41,
    Commit = 42,
    Rollback = 43,
    Transaction = 44,
    Index = 45,
    Unique = 46,
    Order = 47,
    By = 48,
    Asc = 49,
    Desc = 50,
    Limit = 51,
    Offset = 52,
    Group = 53,
    Having = 54,
    Count = 55,
    Sum = 56,
    Min = 57,
    Max = 58,
    Avg = 59,
    Drop = 60,
    Alter = 61,
    Add = 62,
    Column = 63,
    Rename = 64,
    To = 65,
    FloatLiteral = 66,
    In = 67,
    As = 68,
    Like = 69,
    ILike = 70,
    Between = 71,
    Distinct = 72,
    Coalesce = 73,
    Nullif = 74,
    Ifnull = 75,
    Default = 76,
    Autoincrement = 77,
    Serial = 78,
    Replace = 79,
    View = 80,
    Truncate = 81,
    Upper = 82,
    Lower = 83,
    Trim = 84,
    Length = 85,
    Substr = 86,
    Abs = 87,
    Round = 88,
    Ceil = 89,
    Floor = 90,
    Case = 91,
    When = 92,
    Then = 93,
    Else = 94,
    End = 95,
    Exists = 96,
    Union = 97,
    All = 98,
    Intersect = 99,
    Except = 100,
    With = 101,
    Over = 102,
    Partition = 103,
    RowNumber = 104,
    Rank = 105,
    DenseRank = 106,
    Lag = 107,
    Lead = 108,
    Analyze = 109,
    Explain = 110,
    Query = 111,
    Plan = 112,
}
