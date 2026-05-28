using Microsoft.EntityFrameworkCore.Storage;
using System.Text;

namespace Sqlity.EFCore;

/// <summary>
/// Sqlity does not support quoted identifiers, so all delimiter methods return
/// the identifier unquoted.
/// </summary>
public sealed class SqlitySqlGenerationHelper(RelationalSqlGenerationHelperDependencies dependencies)
    : RelationalSqlGenerationHelper(dependencies)
{
    public override string DelimitIdentifier(string identifier) => EscapeIdentifier(identifier);

    public override void DelimitIdentifier(StringBuilder builder, string identifier)
        => builder.Append(EscapeIdentifier(identifier));

    public override string DelimitIdentifier(string name, string? schema)
        => EscapeIdentifier(name);

    public override void DelimitIdentifier(StringBuilder builder, string name, string? schema)
        => builder.Append(EscapeIdentifier(name));
}
