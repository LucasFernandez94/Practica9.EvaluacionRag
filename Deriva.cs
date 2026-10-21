namespace Practica9.EvaluacionRag;

public sealed record ResultadoDrift(string Archivo, bool Igual, string Detalle);

// El copy de la P8 tiene un costo: puede divergir en silencio y las mediciones
// dejan de significar algo. Este check es la red.
//
// La regla de la P9: toda instrumentación es una LÍNEA NUEVA marcada con "// EVAL".
// Ninguna línea original se modifica. Entonces el check es una COMPARACIÓN POR
// SUBSECUENCIA: las líneas del original tienen que aparecer, en orden, dentro
// de las líneas de la copia; las líneas que sobran son inserciones y tienen
// que estar marcadas.
//
//   i = 0
//   para cada línea de la copia (sin líneas en blanco):
//       si i < original.Count y línea == original[i]:  i++      // coincide: es una línea original
//       si no: la línea es una inserción ⇒ tiene que contener "// EVAL"; si no, FALLA
//   al final: si i < original.Count ⇒ faltan N líneas del original ⇒ FALLA
//
// Por qué no "mismo largo + SequenceEqual": ese chequeo comparaba la lista
// COMPLETA, así que una línea original MODIFICADA (con un "// EVAL" al final)
// desaparecía de la comparación y el cambio quedaba INVISIBLE: el grupo D
// quedaba verde en falso justo donde importa. Con la subsecuencia:
//
//   - las líneas agregadas (marcadas) se saltan ✔
//   - una línea original MODIFICADA ya no matchea ⇒ además no matchea hacia
//     adelante ⇒ el original queda sin consumir ⇒ FALLA ✔
//   - una línea original BORRADA ⇒ falta ⇒ FALLA ✔
//
// Las líneas en blanco se ignoran de los dos lados porque no llevan contenido
// semántico. El marcador se matchea EXACTO: "// EVAL". "//EVAL" o "// eval" NO
// cuentan y se tratan como líneas NO marcadas (o sea, como contenido que tiene
// que coincidir con el original).
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

            var orig = Contenido(File.ReadAllLines(pathOrig));
            var copia = Lineas(File.ReadAllLines(pathCopia));

            // La subsecuencia: las líneas del original tienen que consumirse, en
            // orden, dentro de las de la copia.
            var i = 0;
            var inserciones = 0;
            string? problema = null;
            var previaEraInsercion = false;
            var numeroPrevia = 0;
            foreach (var linea in copia)
            {
                if (i < orig.Count && linea.Texto == orig[i].Texto)
                {
                    i++;
                    previaEraInsercion = false;
                    continue;
                }
                if (!linea.Texto.Contains("// EVAL", StringComparison.Ordinal))
                {
                    // Si la línea anterior de la copia era una inserción marcada,
                    // lo más probable es que ÉSTA sea una línea ORIGINAL
                    // modificada (con el "// EVAL" al final): el agujero que el
                    // chequeo viejo no veía.
                    problema = previaEraInsercion
                        ? $"línea {linea.Numero} de la copia: \"{Recorte(linea)}\" no coincide con el " +
                          $"original y no está marcada con // EVAL (y la línea anterior, la {numeroPrevia}, " +
                          $"ES una inserción marcada): parece una línea ORIGINAL modificada — el original " +
                          $"quedó sin consumir"
                        : $"línea {linea.Numero} de la copia: \"{Recorte(linea)}\" no coincide con el " +
                          $"original y no está marcada con // EVAL";
                    break;
                }
                inserciones++;
                previaEraInsercion = true;
                numeroPrevia = linea.Numero;
            }

            if (problema is null && i < orig.Count)
            {
                var siguiente = orig[i];
                problema = $"faltan {orig.Count - i} línea(s) del original (la copia no las tiene, " +
                           $"o las modificó): la primera es la {siguiente.Numero} " +
                           $"\"{Recorte(siguiente)}\"";
            }

            if (problema is null)
                res.Add(new(archivo, true,
                    $"{orig.Count} líneas del original íntegras y en orden (+{inserciones} inserciones // EVAL)"));
            else
                res.Add(new(archivo, false, problema));
        }
        return res;
    }

    // Las líneas en blanco no tienen contenido semántico: se ignoran de los dos lados.
    private static List<Linea> Contenido(string[] lineas) => Lineas(lineas);

    // Las líneas con contenido (las de la copia conservan las "// EVAL": la
    // subsecuencia tiene que VERLAS para poder decir "inserción marcada").
    private static List<Linea> Lineas(string[] lineas) =>
        lineas
            .Select((texto, i) => new Linea(texto, i + 1))
            .Where(l => !string.IsNullOrWhiteSpace(l.Texto))
            .ToList();

    private static string Recorte(Linea linea) =>
        linea.Texto.Length <= 70 ? linea.Texto.Trim() : linea.Texto.Trim()[..70] + "…";

    // Numero = la línea FÍSICA del archivo, para que el mensaje apunte a algo real
    // (el índice dentro del contenido no sirve si hay líneas en blanco en el medio).
    private readonly record struct Linea(string Texto, int Numero);
}
