using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Npgsql;
// EVAL: COPIA de Practica8.RagPostgres/BcLectura.cs @ 500ecad.
// EVAL: Editá SOLO con líneas marcadas // EVAL: el check de deriva compara contra el original.

namespace Practica8.RagPostgres;

// ÚNICO acceso a la DB de BusinessCore: solo SELECT, ninguna escritura.
// Los filtros van siempre en la misma sentencia (fail-closed): el de Name en
// NegociosAsync y el de BusinessId (+ IsDeleted/IsActive) en ServiciosDeAsync.
public sealed class BcLectura(string connStr)
{
    public sealed record Servicio(Guid Id, string Nombre, string? Descripcion, decimal Precio, int DuracionMin);

    public async Task<Dictionary<string, Guid>> NegociosAsync(params string[] nombres)
    {
        await using var conn = new NpgsqlConnection(connStr);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            @"SELECT ""Id"", ""Name"" FROM ""Businesses"" WHERE ""Name"" = ANY(@n)", conn);
        cmd.Parameters.AddWithValue("n", nombres);
        var map = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync()) map[r.GetString(1)] = r.GetGuid(0);
        return map;
    }

    // Id + datos del servicio. El filtro de tenant es parte del SQL, no del código:
    // no existe un método que devuelva servicios sin pasar por businessId.
    public async Task<List<Servicio>> ServiciosDeAsync(Guid businessId)
    {
        await using var conn = new NpgsqlConnection(connStr);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            @"SELECT s.""Id"", s.""Name"", s.""Description"", s.""Price"", s.""DurationMinutes"" " +
            @"FROM ""ServiceItems"" s " +
            @"WHERE s.""BusinessId"" = @b AND s.""IsDeleted"" = false AND s.""IsActive"" = true " +
            @"ORDER BY s.""Name""", conn);
        cmd.Parameters.AddWithValue("b", businessId);
        var lista = new List<Servicio>();
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync())
            lista.Add(new Servicio(r.GetGuid(0), r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2),
                r.GetDecimal(3), r.GetInt32(4)));
        return lista;
    }

    // Texto canónico: idéntico al de la P7 para poder comparar ambas prácticas.
    // InvariantCulture para que el hash no cambie según el locale de la máquina.
    public static string TextoCanonico(Servicio s) =>
        string.Format(CultureInfo.InvariantCulture, "{0}: ${1} ARS, {2} min.{3}",
            s.Nombre, s.Precio, s.DuracionMin,
            string.IsNullOrWhiteSpace(s.Descripcion) ? "" : " " + s.Descripcion);

    // SHA256 en hex minúsculas: la huella que detecta si el texto cambió.
    public static string Hash(string texto) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(texto))).ToLowerInvariant();
}
