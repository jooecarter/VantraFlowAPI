using Microsoft.Data.SqlClient;

namespace VantraFlow.Api.Data;

/// <summary>
/// Raw SqlConnections for the two databases this API spans: <c>Vantra</c> (the shared identity DB that owns
/// UserDetails) and <c>VantraFlow</c> (this product's own data). Callers pick the target explicitly, and no query
/// ever joins across them (prod is Azure SQL). Flow never reads Orbit's or Projects' databases: it talks to them
/// only through their APIs, over a connection.
/// </summary>
public sealed class SqlConnectionFactory(string vantraConnectionString, string flowConnectionString)
{
    /// <summary>Connection to the shared <c>Vantra</c> identity database.</summary>
    public SqlConnection Vantra() => new(vantraConnectionString);

    /// <summary>Connection to the <c>VantraFlow</c> product database.</summary>
    public SqlConnection Flow() => new(flowConnectionString);
}
