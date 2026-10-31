using Barotrauma;
using Barotrauma.Networking;

namespace SubSeal.Server
{
    
    internal static class Identity
    {
        internal static string Of(NetworkConnection connection)
        {
            if (connection is null) { return null; }

            if (GameMain.Server?.ConnectedClients is not null)
            {
                foreach (Client client in GameMain.Server.ConnectedClients)
                {
                    if (client.Connection != connection) { continue; }
                    if (client.AccountId.TryUnwrap(out AccountId accountId)) { return accountId.StringRepresentation; }
                    break;
                }
            }

            return connection.Endpoint?.StringRepresentation;
        }
    }
}
