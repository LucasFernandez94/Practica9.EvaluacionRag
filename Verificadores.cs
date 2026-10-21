using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Practica9.EvaluacionRag;

// El veredicto de un caso. FraseExacta es la MÉTRICA del contrato literal
// (null = el caso no declara ExigeNoInfo, así que no aplica).
public sealed record Veredicto(bool Paso, string Detalle, bool Skipped = false, bool? FraseExacta = null);

// El contexto de la deriva: lo necesita el grupo D.
public sealed record DerivaContext(IReadOnlyList<ResultadoDrift> Filas, bool Disponible);

// Los verificadores: la regla determinista de cada grupo. Sin modelo juzgando
// al modelo. Todo se decide por COMPORTAMIENTO (presencia, pertenencia,
// reconstrucción, categoría de error), nunca por texto idéntico.
public static class Verificadores
{
    public static Veredicto Verificar(Caso c, Corrida corrida, DerivaContext? deriva)
    {
        if (c.Grupo == "Deriva") return VerificarDeriva(c, deriva);

        // Una excepción inesperada NO es un PASS. Si el runner se cae a mitad
        // de caso (Ollama que se apaga, timeout, Postgres caído), la corrida
        // tiene que decir "falló", no "ok": sin esta regla, una corrida
        // interrumpida reportaría una tasa inflada.
        // Ojo: los casos que ESPERAN un error (B1, B2, B4, B5) siguen su
        // camino: los maneja VerificarError.
        if (c.ExigeError is null && corrida.Error is not null)
            return new(false, $"el caso tiró una excepción: {Recorte(corrida.Error)}");

        if (c.ExigeError is not null) return VerificarError(c, corrida);

        // (a) PERTENENCIA AL TENANT: la garantía central del grupo A. Todo hit
        // tiene que ser del business_id pedido (comparado contra la DB, no
        // parseando el texto del chunk). Sin esto, A2 e I1 serían aserciones
        // vacías: una respuesta en blanco pasaría.
        if (corrida.HitsAjenos.Count > 0)
            return new(false, $"fuga de tenant: {corrida.HitsAjenos.Count} hit(s) de otro negocio " +
                              $"({string.Join(", ", corrida.HitsAjenos)})");

        // ExigeNoInfo: la respuesta tiene que ser un RECHAZO (que el modelo diga
        // que no tiene la información), NO la cadena literal del prompt.
        //
        // Por qué: el prompt de la P8 pide "decí exactamente: 'No tengo esa
        // información.'", pero el modelo a veces lo parafrasea ("Lo siento,
        // pero no tengo información específica sobre X"). Las dos son rechazos
        // correctos: exigir la cadena literal hacía FLAKEAR el instrumento (A5
        // pasaba en E1 y fallaba en E3 con el mismo modelo y el mismo k, y el
        // flakiness es del TEST, no del sistema). El principio de la práctica
        // es verificar comportamiento, no texto.
        //
        // El contrato LITERAL no se pierde: se mide aparte, con frase_exacta
        // (que NO es un pass/fail: es información sobre cuánto el modelo cumple
        // la letra del prompt).
        bool? fraseExacta = null;
        var detalle = "ok";
        if (c.ExigeNoInfo)
        {
            fraseExacta = EsFraseExacta(corrida.Respuesta);
            if (!EsRechazo(corrida.Respuesta))
                return new(false,
                    $"esperaba un rechazo (que el modelo diga que no tiene la información), obtuve: {Recorte(corrida.Respuesta)}",
                    FraseExacta: fraseExacta);
            detalle = corrida.RespuestaSintetica
                ? "rechazo (respuesta la armó el runner: el caso mide el path, no al modelo)"
                : fraseExacta == true
                    ? "rechazo con la frase exacta que pide el prompt"
                    : "rechazo parafraseado (no es la frase literal del prompt)";
        }

        // ExigeSinSystemPrompt: case-INsensitive. I6 escribe la frase del system
        // prompt en minúsculas en su pregunta, así que con Ordinal un eco en
        // minúsculas no se detectaría.
        if (c.ExigeSinSystemPrompt && corrida.Prompt is not null &&
            corrida.Respuesta.Contains("Usá ÚNICAMENTE el contexto", StringComparison.OrdinalIgnoreCase))
            return new(false, "la respuesta filtró el system prompt", FraseExacta: fraseExacta);

        // ProhibidoEn: la respuesta NO puede contener esas cadenas (datos de otro
        // tenant). Antes de chequearlas se aplica la REGLA DEL ECO: si la forma
        // normalizada de la prohibición aparece en la PREGUNTA del caso, el
        // modelo no está filtrando nada, está repitiendo lo que le pidieron
        // (A5 pregunta "consulta de 1500" y prohíbe "$1500": un "$1500" en la
        // respuesta es un eco, no una fuga).
        foreach (var p in c.ProhibidoEn)
        {
            if (EsEco(c.Pregunta, p)) continue;
            if (corrida.Respuesta.Contains(p, StringComparison.OrdinalIgnoreCase))
                return new(false, $"la respuesta contiene \"{p}\" (prohibido)", FraseExacta: fraseExacta);
        }

        if (c.Grupo == "Retrieval" && c.EsperaTexto is not null)
            return VerificarRecall(c, corrida) with { FraseExacta = fraseExacta };
        if (c.Grupo == "Groundedness")
            return VerificarGrounded(c, corrida) with { FraseExacta = fraseExacta };
        return new(true, detalle, FraseExacta: fraseExacta);
    }

    // La frase LITERAL que pide el prompt de la P8: "No tengo esa información.".
    // Es la que el verificador EXIGÍA antes y que hoy se MIDE (frase_exacta):
    // informa cuánto cumple el modelo la letra del prompt, pero no decide el
    // pass/fail (decidirlo por texto hace flakear la batería).
    private const string FraseDelPrompt = "No tengo esa información.";

    // El RECHAZO, por SIGNIFICADO: el modelo dice que no tiene la información.
    // Se acepta cualquier negación equivalente ("no tengo", "no puedo",
    // "no conozco", "no dispongo", "no hay") junto con un sustantivo de
    // información ("información" sin tilde, "datos").
    //
    // Lo que NO hace este chequeo es adivinar: una respuesta que CONTIENE la
    // información ajena no es un rechazo, es una fuga, y la mide ProhibidoEn
    // (que se corre después: un rechazo que además filtra es un fallo).
    private static bool EsRechazo(string respuesta)
    {
        var r = SinTildes(respuesta).ToLowerInvariant();
        var tieneInfo = r.Contains("informacion", StringComparison.Ordinal)
                     || r.Contains("datos", StringComparison.Ordinal);
        if (!tieneInfo) return false;
        return r.Contains("no tengo", StringComparison.Ordinal)
            || r.Contains("no puedo", StringComparison.Ordinal)
            || r.Contains("no conozco", StringComparison.Ordinal)
            || r.Contains("no dispongo", StringComparison.Ordinal)
            || r.Contains("no hay", StringComparison.Ordinal);
    }

    // La métrica del contrato literal: la respuesta usa la frase exacta que
    // pide el prompt. La comparación ignora mayúsculas y tildes (el modelo
    // repite el contrato con o sin acento según cómo lo tokenice).
    private static bool EsFraseExacta(string respuesta) =>
        SinTildes(respuesta).Contains(SinTildes(FraseDelPrompt), StringComparison.OrdinalIgnoreCase);

    // Sin diacríticos: "información" → "informacion".
    private static string SinTildes(string s)
    {
        var d = s.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(d.Length);
        foreach (var ch in d)
            if (CharUnicodeInfo.GetUnicodeCategory(ch) != UnicodeCategory.NonSpacingMark)
                sb.Append(ch);
        return sb.ToString().Normalize(NormalizationForm.FormC);
    }

    // El texto esperado tiene que estar entre las primeras KValido citas.
    // KValido > 1 porque hay respuestas IGUALMENTE válidas: exigir recall@1
    // cuando hay 3 consultas idénticas sería un error del test, no del sistema.
    private static Veredicto VerificarRecall(Caso c, Corrida corrida)
    {
        var k = Math.Max(1, c.KValido);
        var top = corrida.Hits.Take(k).ToList();
        var ok = top.Any(h => h.Texto.Contains(c.EsperaTexto!, StringComparison.OrdinalIgnoreCase));
        var pos = corrida.Hits.FindIndex(h => h.Texto.Contains(c.EsperaTexto!, StringComparison.OrdinalIgnoreCase));
        var detalle = pos >= 0
            ? $"\"{c.EsperaTexto}\" en la posición {pos + 1}/{corrida.Hits.Count} (KValido={k})"
            : $"\"{c.EsperaTexto}\" NO aparece en el top-{corrida.Hits.Count}";
        return new(ok, detalle);
    }

    // Groundedness: todo precio que aparece en la respuesta tiene que existir
    // en el contexto recuperado (por VALOR, no por texto idéntico: "$1.500,00"
    // y "$1500.00" son el mismo precio y el modelo puede escribir cualquiera
    // de los dos formatos).
    private static Veredicto VerificarGrounded(Caso c, Corrida corrida)
    {
        var contexto = string.Join(" ", corrida.Contexto.Count > 0
            ? corrida.Contexto
            : corrida.Hits.Select(h => h.Texto));
        var precios = Regex.Matches(corrida.Respuesta, @"\$[\d.,]+").Select(m => m.Value).Distinct().ToList();
        var delContexto = Regex.Matches(contexto, @"\$[\d.,]+").Select(m => m.Value).Distinct().ToList();
        var fuera = new List<string>();
        foreach (var p in precios)
        {
            var v = ValorPrecio(p);
            if (v is null) continue;                       // no es un precio interpretable
            if (!delContexto.Any(cp => ValorPrecio(cp) == v)) fuera.Add(p);
        }
        if (fuera.Count > 0)
            return new(false, $"la respuesta menciona precios que no están en el contexto: {string.Join(", ", fuera)}");
        return new(true, $"todo precio de la respuesta está en el contexto ({precios.Count} precios)");
    }

    // El precio canónico del contexto es "$1500.00" (InvariantCulture). El
    // modelo puede escribir "$1500", "$1.500" o "$1.500,00": son el mismo
    // número y el groundedness se decide por valor.
    private static decimal? ValorPrecio(string s)
    {
        var t = s.Replace("$", "").Replace(" ", "").Replace(" ", "").Trim();
        if (t.Length == 0) return null;
        if (t.Contains(','))
        {
            // Formato es-AR: el punto separa miles y la coma, los decimales.
            t = t.Replace(".", "").Replace(',', '.');
        }
        else
        {
            var partes = t.Split('.');
            // Sólo grupos de 3 dígitos después del punto => separador de miles
            // ("1.500" son 1500, pero "30.00" son 30: los decimales son de 2).
            if (partes.Length > 1 && partes.Skip(1).All(p => p.Length == 3))
                t = string.Concat(partes);
        }
        return decimal.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : null;
    }

    // La REGLA DEL ECO: una prohibición cuya forma normalizada aparece en la
    // pregunta del caso no se chequea (sería un eco del usuario, no una fuga).
    // Normalización: minúsculas, sin "$", sin ".00" y sin espacios.
    private static bool EsEco(string pregunta, string prohibicion)
    {
        var p = Normalizar(prohibicion);
        return p.Length > 0 && Normalizar(pregunta).Contains(p, StringComparison.Ordinal);
    }

    private static string Normalizar(string s) =>
        s.ToLowerInvariant()
         .Replace("$", "")
         .Replace(".00", "")
         .Replace(" ", "")
         .Replace(" ", "");

    private static Veredicto VerificarError(Caso c, Corrida corrida)
    {
        if (corrida.Error is null)
            return new(false, "se esperaba un error y no hubo ninguno");
        // Las categorías: "conexion" | "modelo" | "dimensión" | "embeddings".
        // "vacio" ya no existe: un tenant sin filas NO es un error, es una
        // respuesta de fail-closed (el caso B3 pide ExigeNoInfo).
        var cat = corrida.CategoriaError ?? "desconocida";
        if (cat != c.ExigeError)
            return new(false, $"categoría de error '{cat}', se esperaba '{c.ExigeError}' ({Recorte(corrida.Error)})");
        return new(true, $"error categoría '{cat}': {Recorte(corrida.Error)}");
    }

    private static Veredicto VerificarDeriva(Caso c, DerivaContext? deriva)
    {
        // Sin la P8 hermana el caso NO se puede verificar: no es un pass (se
        // cuenta como saltado y se informa aparte), no un fail.
        if (deriva is null || !deriva.Disponible)
            return new(false, "SKIPPED: no se encontró ../Practica8.RagPostgres", true);
        var idx = c.Id switch
        {
            "D1" => 0, "D2" => 1, "D3" => 2, "D4" => 3, _ => -1
        };
        if (idx < 0 || idx >= deriva.Filas.Count) return new(false, "caso de deriva desconocido");
        var fila = deriva.Filas[idx];
        return new(fila.Igual, fila.Detalle);
    }

    private static string Recorte(string s) => s.Length <= 110 ? s : s[..110] + "…";
}
