# ADO.NET provider notes

The ADO.NET provider will sit on top of the query engine and expose standard database APIs without re-implementing core storage logic.

## Planned surfaces

- `DbConnection`
- `DbCommand`
- `DbParameter`
- `DbDataReader`

## Milestone dependency

This layer starts from the now-working storage/query MVP and should turn it into standard `DbConnection`, `DbCommand`, and `DbDataReader` surfaces instead of re-implementing execution logic.
