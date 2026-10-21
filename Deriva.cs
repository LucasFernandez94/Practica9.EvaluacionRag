namespace Practica9.EvaluacionRag;

public sealed record ResultadoDrift(string Archivo, bool Igual, string Detalle);

// El copy de la P8 tiene un costo: puede divergir en silencio y las mediciones
// dejan de significar algo. Este check es la red.
//
// La regla de la P9: toda instrumentación es una LÍNEA NUEVA marcada con "// EVAL".
// Ninguna línea original se modifica. Entonces el check es una IGUALDAD EXACTA de
// las líneas CON CONTENIDO:
//
//   copia − (líneas "// EVAL") − (líneas en blanco)
//     ==  original − (líneas en blanco)
//
// Las líneas en blanco se ignoran de los dos lados porque no llevan contenido
// semántico. La única divergencia que eso deja pasar es "alguien borró una línea
// en blanco del original", que no cambia el comportamiento del código.
//
// El marcador se matchea EXACTO: "// EVAL". "//EVAL" o "// eval" NO cuentan y se
// tratan como líneas NO marcadas (o sea, como contenido que tiene que coincidir).
public static class Deriva
{
    public static IReadOnlyList<string> Archivos { get; } =
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

            var orig = Contenido(File.ReadAllLines(pathOrig), descartarEval: false);
            var copia = Contenido(File.ReadAllLines(pathCopia), descartarEval: true);

            if (orig.Count == copia.Count && orig.Select(l => l.Texto).SequenceEqual(copia.Select(l => l.Texto)))
            {
                res.Add(new(archivo, true, $"{orig.Count} líneas de contenido idénticas"));
            }
            else
            {
                var primero = 0;
                var max = Math.Min(orig.Count, copia.Count);
                while (primero < max && orig[primero].Texto == copia[primero].Texto) primero++;
                var num = primero < copia.Count ? copia[primero].Numero
                         : primero < orig.Count ? orig[primero].Numero
                         : 0;
                var det = $"difieren en la línea {num}: " +
                          $"orig=\"{Recorte(orig, primero)}\" copia=\"{Recorte(copia, primero)}\"";
                res.Add(new(archivo, false, det));
            }
        }
        return res;
    }

    // Las líneas en blanco no tienen contenido semántico: se ignoran de los dos lados.
    // Las líneas marcadas "// EVAL" son instrumentación de la P9: sólo se descartan de
    // la copia (el original de la P8 no puede tenerlas).
    private static List<Linea> Contenido(string[] lineas, bool descartarEval) =>
        lineas
            .Select((texto, i) => new Linea(texto, i + 1))
            .Where(l => !string.IsNullOrWhiteSpace(l.Texto))
            .Where(l => !descartarEval || !l.Texto.Contains("// EVAL", StringComparison.Ordinal))
            .ToList();

    private static string Recorte(List<Linea> lineas, int i) =>
        i < lineas.Count ? lineas[i].Texto.Trim() : "(fin de archivo)";

    // Numero = la línea FÍSICA del archivo, para que el mensaje apunte a algo real
    // (el índice dentro del contenido no sirve si hay líneas en blanco en el medio).
    private readonly record struct Linea(string Texto, int Numero);
}
