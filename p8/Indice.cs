using Npgsql;

namespace Practica8.RagPostgres;

// Índice vectorial en Postgres (pgvector). Es un artefacto DERIVADO de BusinessCore:
// la fuente de verdad sigue siendo BC.
// EnsureTableAsync garantiza el ESQUEMA del índice (extensión + tabla + su índice) de
// forma idempotente; SyncAsync lo repuebla desde BC embebiendo solo lo nuevo o lo que cambió.
public sealed class Indice(string connStr)
{
    public async Task EnsureTableAsync()
    {
        await using var conn = new NpgsqlConnection(connStr);
        await conn.OpenAsync();

        // Una sentencia por comando: el protocolo extendido de Postgres no acepta
        // varias sentencias en un solo command con parámetros.
        await EjecutarAsync(conn, "CREATE EXTENSION IF NOT EXISTS vector;");
        await EjecutarAsync(conn, """
            CREATE TABLE IF NOT EXISTS bc_service_chunks (
                service_id   uuid PRIMARY KEY,      -- Id real de ServiceItems en BC
                business_id  uuid NOT NULL,         -- filtro de tenant, DENTRO del query
                hash         char(64) NOT NULL,     -- SHA256 del texto -> detecta cambios
                texto        text NOT NULL,         -- texto canónico que va al prompt
                embedding    vector(768) NOT NULL   -- nomic-embed-text
            );
            """);
        await EjecutarAsync(conn,
            "CREATE INDEX IF NOT EXISTS ix_bc_service_chunks_business ON bc_service_chunks (business_id);");
    }

    private static async Task EjecutarAsync(NpgsqlConnection conn, string sql)
    {
        await using var cmd = new NpgsqlCommand(sql, conn);
        await cmd.ExecuteNonQueryAsync();
    }

    public sealed record SyncReport(int Nuevos, int Actualizados, int Borrados);

    // Sync idempotente: compara el hash indexado contra el hash actual de BC.
    // Solo se embebe lo que es nuevo o cambió; lo que se borró en BC se purga del índice.
    public async Task<SyncReport> SyncAsync(
        Guid businessId,
        List<(Guid Id, string Texto, string Hash)> actuales,
        Embeddings embeddings)
    {
        var indexados = await IndexadosAsync(businessId);

        var nuevos = actuales.Where(a => !indexados.ContainsKey(a.Id)).ToList();
        var modificados = actuales.Where(a => indexados.TryGetValue(a.Id, out var h) && h != a.Hash).ToList();
        var borrados = indexados.Keys.Where(id => actuales.All(a => a.Id != id)).ToList();

        // Un solo request de embeddings para todo lo que hay que guardar.
        var aGuardar = nuevos.Concat(modificados).ToList();
        if (aGuardar.Count > 0)
        {
            var vectores = await embeddings.EmbedDocumentosAsync(aGuardar.Select(a => a.Texto).ToList());
            // Si Ollama devuelve menos embeddings que textos, indexar por posición
            // reventaría con IndexOutOfRangeException y el error que ve el usuario
            // sería opaco. Se valida acá, antes de usar los vectores.
            if (vectores.Count != aGuardar.Count)
                throw new InvalidOperationException(
                    $"Ollama devolvió {vectores.Count} embeddings para {aGuardar.Count} textos.");

            await using var conn = new NpgsqlConnection(connStr);
            await conn.OpenAsync();
            for (var i = 0; i < aGuardar.Count; i++)
            {
                var (id, texto, hash) = aGuardar[i];
                await using var cmd = new NpgsqlCommand(
                    @"INSERT INTO bc_service_chunks (service_id, business_id, hash, texto, embedding)
                      VALUES (@sid, @bid, @hash, @texto, @v)
                      ON CONFLICT (service_id) DO UPDATE
                      SET business_id = EXCLUDED.business_id,
                          hash        = EXCLUDED.hash,
                          texto       = EXCLUDED.texto,
                          embedding   = EXCLUDED.embedding", conn);
                cmd.Parameters.AddWithValue("sid", id);
                cmd.Parameters.AddWithValue("bid", businessId);
                cmd.Parameters.AddWithValue("hash", hash);
                cmd.Parameters.AddWithValue("texto", texto);
                cmd.Parameters.AddWithValue("v", vectores[i]);   // float[] -> vector (sin cast)
                await cmd.ExecuteNonQueryAsync();
            }
        }

        if (borrados.Count > 0)
        {
            await using var conn = new NpgsqlConnection(connStr);
            await conn.OpenAsync();
            foreach (var id in borrados)
            {
                // El DELETE va con el tenant: nunca toca filas de otro negocio.
                await using var cmd = new NpgsqlCommand(
                    "DELETE FROM bc_service_chunks WHERE service_id = @sid AND business_id = @bid", conn);
                cmd.Parameters.AddWithValue("sid", id);
                cmd.Parameters.AddWithValue("bid", businessId);
                await cmd.ExecuteNonQueryAsync();
            }
        }

        return new SyncReport(nuevos.Count, modificados.Count, borrados.Count);
    }

    // Estado actual del índice para un tenant: service_id -> hash.
    // El hash es char(64) (bpchar, rellenado con espacios): se Trim() antes de comparar.
    private async Task<Dictionary<Guid, string>> IndexadosAsync(Guid businessId)
    {
        await using var conn = new NpgsqlConnection(connStr);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "SELECT service_id, hash FROM bc_service_chunks WHERE business_id = @b", conn);
        cmd.Parameters.AddWithValue("b", businessId);
        var map = new Dictionary<Guid, string>();
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync()) map[r.GetGuid(0)] = r.GetString(1).Trim();
        return map;
    }

    public sealed record Hit(Guid ServiceId, string Texto, double Score);

    // Retrieval en SQL. El filtro de tenant va DENTRO de la misma sentencia que el
    // ORDER BY: si el filtro se aplicara después del rankeo, el chunk más cercano
    // podría ser de otro negocio y devolverlo ya sería una fuga de tenant.
    public async Task<List<Hit>> BuscarAsync(Guid businessId, float[] consulta, int maximo = 3)
    {
        await using var conn = new NpgsqlConnection(connStr);
        await conn.OpenAsync();

        // Regla adaptativa de P7: catálogo chico (≤8) se manda COMPLETO; grande, top-3.
        int total;
        await using (var cmd = new NpgsqlCommand(
            "SELECT count(*) FROM bc_service_chunks WHERE business_id = @b", conn))
        {
            cmd.Parameters.AddWithValue("b", businessId);
            total = (int)(long)(await cmd.ExecuteScalarAsync())!;
        }
        if (total == 0) return [];                      // fail-closed: sin filas no hay respuesta

        // maximo <= 0 daría LIMIT 0 → lista vacía → un "No tengo esa información."
        // falso. El corte mínimo es 1 chunk.
        var k = total <= 8 ? total : Math.Max(1, maximo);

        await using var rank = new NpgsqlCommand(
            @"SELECT service_id, texto, 1 - (embedding <=> @q::vector) AS score
              FROM bc_service_chunks
              WHERE business_id = @b
              ORDER BY embedding <=> @q::vector
              LIMIT @k", conn);
        // Un solo parámetro para el vector: en el INSERT el float[] va sin cast (Postgres
        // lo convierte con el cast de ASIGNACIÓN real[] -> vector), pero acá el cast
        // ::vector es OBLIGATORIO porque sin él Postgres responde 42883:
        // operator does not exist: vector <=> real[].
        rank.Parameters.AddWithValue("q", consulta);
        rank.Parameters.AddWithValue("b", businessId);
        rank.Parameters.AddWithValue("k", k);

        var hits = new List<Hit>();
        await using var r = await rank.ExecuteReaderAsync();
        while (await r.ReadAsync())
            hits.Add(new Hit(r.GetGuid(0), r.GetString(1), r.GetDouble(2)));
        return hits;
    }
}
