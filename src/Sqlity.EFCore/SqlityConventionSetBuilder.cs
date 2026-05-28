using Microsoft.EntityFrameworkCore.Metadata.Conventions.Infrastructure;

namespace Sqlity.EFCore;

public class SqlityConventionSetBuilder(
    ProviderConventionSetBuilderDependencies dependencies,
    RelationalConventionSetBuilderDependencies relationalDependencies)
    : RelationalConventionSetBuilder(dependencies, relationalDependencies);
