namespace Practica9.EvaluacionRag;

public sealed record ResultadoDrift(string Archivo, bool Igual, string Detalle);

// El copy de la P8 tiene un costo: puede divergir en silencio y las mediciones
// dejan de significar algo. Este check es la red.
//
// La regla de la P9: toda instrumentación es una LÍNEA NUEVA marcada con "// EVAL".
// Ninguna línea original se modifica. Entonces el check es una IGUALDAD EXACTA:
//   copia − (líneas con "// EVAL")  ==  original
public static class Deriva
{
    public static readonly string[] Archivos =
        ["Indice.cs", "BcLectura.cs", "Embeddings.cs", "Asistente.cs"];

    // La P8 tiene que estar en la carpeta hermana. Si no está, el check se SKIPEA:
    // la batería tiene que poder correr sola.
    public static string? RaizP8Desde(string raizP9)
    {
        var carpeta = Path.GetFullPath(Path.Combine(raizP9, "..", "Practica8.RagPostgres"));
        return Directory.Exists(carpeta) ? carpeta : null;
    }

    public static IReadOnlyList<ResultadoDrift> Verificar(string raizP8, string raizP9)
    {
        var res = new List<ResultadoDrift>();
        foreach (var archivo in Archivos)
        {
            var pathOrig = Path.Combine(raizP8, archivo);
            var pathCopia = Path.Combine(raizP9, "p8", archivo);
            if (!File.Exists(pathOrig)) { res.Add(new(archivo, false, "no existe el original")); continue; }
            if (!File.Exists(pathCopia)) { res.Add(new(archivo, false, "no existe la copia")); continue; }

            var orig = File.ReadAllLines(pathOrig);
            var copia = File.ReadAllLines(pathCopia)
                .Where(l => !l.Contains("// EVAL", StringComparison.Ordinal))
                .ToArray();

            if (orig.Length == copia.Length && orig.SequenceEqual(copia))
            {
                res.Add(new(archivo, true, $"{orig.Length} líneas idénticas"));
            }
            else
            {
                var primero = 0;
                var max = Math.Min(orig.Length, copia.Length);
                while (primero < max && orig[primero] == copia[primero]) primero++;
                var det = $"difieren en la línea {primero + 1}: " +
                          $"orig=\"{Recorte(orig, primero)}\" copia=\"{Recorte(copia, primero)}\"";
                res.Add(new(archivo, false, det));
            }
        }
        return res;
    }

    private static string Recorte(string[] lineas, int i) =>
        i < lineas.Length ? lineas[i].Trim() : "(fin de archivo)";
}
