# sqlcheck

Parses every SQL batch the API can send, and every `db/*.sql` migration, with
Microsoft's own T-SQL parser (ScriptDom) at level 170 and at 160 (Azure SQL), so
a syntax error fails here instead of in production. Nothing connects to a
database.

    scripts/sqlcheck.sh                      # or: dotnet run --project tools/sqlcheck
    scripts/sqlcheck.sh --warnings           # also list each ODBC / future reserved word
    scripts/sqlcheck.sh --verbose            # every query site and how many shapes it has
    scripts/sqlcheck.sh --dump /tmp/sql      # write every checked batch to a file
    scripts/sqlcheck.sh --file new.sql       # check an ad hoc script (GO-separated)

Exit code 0 = clean, 1 = errors, 2 = the API source didn't compile.

## What it checks

- **Migrations**: each file split on `GO` lines, each batch parsed.
- **Every Dapper call** (`Query*` / `Execute*` / `QueryMultiple*`, `CommandDefinition`):
  the SQL argument is worked out from the source with Roslyn, the way it is
  built at run time — constants (folded), `static readonly` fields (read from
  the built assembly by reflection, so their real values), interpolations and
  `+`, locals (every value assigned), every branch of `? :` / `??` / `switch`,
  `string.Format` / `Join` / `Concat`, and the API's own helpers (static
  `…Sql` helpers are run by reflection with the caller's arguments; others are
  inlined). A parameter of the querying method is resolved from its callers.
  Each distinct shape of the batch is parsed (e.g. before / after db/009).
- **Dapper rewrites** are applied first: a list parameter (`IN @Ids` with an
  `IEnumerable`) becomes `IN (@Ids1, @Ids2)`; `{=Name}` literals are inlined.
- **Reserved words**: an unquoted identifier (column, table, alias, CTE, OPENJSON
  column…) that is a T-SQL reserved keyword is an error even where the parser
  lets it through — write `[Bulk]`. ODBC / future reserved keywords are listed
  as warnings.
- **Undeclared variables**: when the call's parameters are an anonymous object,
  every `@name` the batch reads must be sent or `DECLARE`d ("Must declare the
  scalar variable").
- **SQL fields**: every `static` SQL string in `Data/` (or named `…Sql`/`…Cols`)
  must parse on its own, or have been checked inside a batch that uses it.

If a piece of SQL is built from something the checker can't trace, it fails
with "can't tell what text … puts into the SQL": give it a value in
`HoleValues.cs`.

It does not know the schema: a misspelt column or table still parses.
