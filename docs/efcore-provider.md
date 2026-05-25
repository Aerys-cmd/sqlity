# EF Core provider notes

The EF Core provider is the last layer because it depends on both the ADO.NET provider and enough relational behavior to translate EF Core operations sensibly.

## Planned responsibilities

- register provider services
- map EF Core type system to Sqlity storage/query capabilities
- translate a narrow first subset of LINQ/query expressions

## Milestone dependency

This layer starts after the ADO.NET provider exposes stable command execution, schema metadata, and enough relational semantics for EF Core query translation.
