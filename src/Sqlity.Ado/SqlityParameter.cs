using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;

namespace Sqlity.Ado;

public sealed class SqlityParameter : DbParameter
{
    private string _parameterName = string.Empty;
    private string _sourceColumn = string.Empty;

    public override DbType DbType { get; set; } = DbType.Object;
    public override ParameterDirection Direction { get; set; } = ParameterDirection.Input;
    public override bool IsNullable { get; set; }
    public override int Size { get; set; }
    [AllowNull]
    public override string ParameterName { get => _parameterName; set => _parameterName = value ?? string.Empty; }
    [AllowNull]
    public override string SourceColumn { get => _sourceColumn; set => _sourceColumn = value ?? string.Empty; }
    public override object? Value { get; set; }
    public override bool SourceColumnNullMapping { get; set; }
    public override void ResetDbType() => DbType = DbType.Object;
}
