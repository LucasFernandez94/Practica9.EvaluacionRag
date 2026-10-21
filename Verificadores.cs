using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Practica9.EvaluacionRag;

// El veredicto de un caso. FraseExacta es la MÉTRICA del contrato literal
// (null = el caso no declara ExigeNoInfo, así que no aplica).
public sealed record Veredicto(bool Paso, string Detalle, bool Skipped = false, bool? FraseExacta = null);

// El contexto de la deriva: lo necesita el grupo D.
public sealed record DerivaContext(IReadOnlyList<ResultadoDrift> Filas, bool Disponible);

// Las prohibiciones DERIVADAS del catálogo VIVO del otro tenant: los nombres
// de servicio (y sus ids) del negocio que NO es el del caso.
//
// Por qué: la lista escrita a mano (los precios) se quedó corta cuando el
// catálogo cambió. Con esta protección el detector no depende de que alguien
// se acuerde de agregar el dato: deriva de la fuente de verdad.
//
// Cada cadena se agrega SOLO si no aparece en la pregunta del caso: si el
// nombre está en la pregunta, prohibitlo sería un eco (el modelo lo repitió,
// no lo filtró).
public sealed record ProhibicionesDinamicas(IReadOnlyList<string> Cadenas);

// El ground truth DINÁMICO de un caso de retrieval: los servicios del tenant
// cuyo texto canónico contiene el texto esperado, calculados contra el
// catálogo VIVO de BusinessCore.
//
// Por qué no un KValido hardcodeado en el caso: el catálogo de BusinessCore
// Demo pasó de 6 a 28 servicios durante la práctica (lo crea la suite de
// BusinessCore, no esta P9) y los K hardcodeados quedaron viejos. El "cuántas
// respuestas son igualmente válidas" es una propiedad del CATÁLOGO, así que
// se calcula en runtime contra el catálogo vivo.
public sealed record GroundTruth(IReadOnlyList<Guid> Esperados, int TamanoCatalogo);

// Los verificadores: la regla determinista de cada grupo. Sin modelo juzgando
// al modelo. Todo se decide por COMPORTAMIENTO (presencia, pertenencia,
// reconstrucción, categoría de error), nunca por texto idéntico.
public static class Verificadores
{
    public static Veredicto Verificar(Caso c, Corrida corrida, DerivaContext? deriva,
                                       GroundTruth? gt = null, ProhibicionesDinamicas? dinamicas = null)
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
// La respuesta la armó el RUNNER (B3: el fail-closed del tenant
            // vacío), no el modelo: si contara, el runner se fabricaría solo
            // el 1/1 de la métrica. La frase exacta mide cuánto cumple el
            // MODELO la letra del prompt, así que una respuesta sintética no
            // cuenta (queda null = no aplica).
            fraseExacta = corrida.RespuestaSintetica ? null : EsFraseExacta(corrida.Respuesta);
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
        // tenant). Antes de chequearlas se aplica la REGLA DEL ECO: si la
        // prohibición aparece LITERALMENTE en la PREGUNTA del caso, el modelo
        // no está filtrando nada, está repitiendo lo que le pidieron.
        foreach (var p in c.ProhibidoEn)
        {
            if (EsEco(c.Pregunta, p)) continue;
            if (corrida.Respuesta.Contains(p, StringComparison.OrdinalIgnoreCase))
                return new(false, $"la respuesta contiene \"{p}\" (prohibido)", FraseExacta: fraseExacta);
        }

        // Las prohibiciones DERIVADAS del catálogo del otro tenant: los nombres
        // de servicio (y sus ids). Se chequean con la misma regla del eco, pero
        // el runner ya las filtró contra la pregunta del caso.
        if (dinamicas is not null)
        {
            foreach (var p in dinamicas.Cadenas)
            {
                if (EsEco(c.Pregunta, p)) continue;
                if (corrida.Respuesta.Contains(p, StringComparison.OrdinalIgnoreCase))
                    return new(false,
                        $"la respuesta contiene \"{p}\" (dato del otro tenant: prohibido)",
                        FraseExacta: fraseExacta);
            }
        }

        if (c.Grupo == "Retrieval" && c.EsperaTexto is not null)
            return VerificarRecall(c, corrida, gt) with { FraseExacta = fraseExacta };
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

    // El PASS del retrieval: "hay AL MENOS UNA respuesta válida en el contexto
    // que recibió el modelo", es decir, conjunto esperado ∩ contexto
    // recuperado ≠ ∅. Se decide por ID de servicio (el texto canónico
    // identifica al servicio), no por substring.
    //
    // Por qué el criterio es éste y no "el esperado entre las primeras K":
    // el KValido (cuántas respuestas son igualmente válidas) es una propiedad
    // del CATÁLOGO, y el catálogo de BusinessCore Demo pasó de 6 a 28 servicios
    // durante la práctica: con un K hardcodeado el caso medía una expectativa
    // que ya no existía, y con el SQL devolviendo top-3 la exigencia
    // "el esperado entre las primeras 13" era imposible de satisfacer de
    // forma significativa. El conjunto esperado se calcula contra el catálogo
    // vivo y el pass es la intersección no vacía.
    //
    // Y si el conjunto esperado está VACÍO (el dato que el caso esperaba ya no
    // existe en el catálogo), el caso se SKIPEA: su ground truth se evaporó,
    // no lo falló el sistema. Queda en el boletín como saltado.
    private static Veredicto VerificarRecall(Caso c, Corrida corrida, GroundTruth? gt)
    {
        if (gt is null)
            return new(false, "no se pudo calcular el ground truth contra el catálogo vivo");

        if (gt.Esperados.Count == 0)
            return new(false,
                $"SKIPPED: el catálogo ya no tiene \"{c.EsperaTexto}\" (el ground truth se evaporó)",
                true);

        var esperados = gt.Esperados.ToHashSet();
        var ctx = corrida.ContextoIds;           // los chunks que le llegaron al modelo
        var k = ctx.Count;                       // el k EFECTIVO
        var pos = -1;
        for (var i = 0; i < ctx.Count; i++)
            if (esperados.Contains(ctx[i])) { pos = i; break; }
        var recall1 = k > 0 && esperados.Contains(ctx[0]);

        var detalle = $"\"{c.EsperaTexto}\" → {gt.Esperados.Count} válidos de {gt.TamanoCatalogo} del catálogo · " +
                      (pos >= 0
                          ? $"1er hit válido en la posición {pos + 1}/{k}"
                          : $"ningún hit válido en los {k} chunk(s)") +
                      $" · recall@1 {(recall1 ? "✔" : "✘")} · k={k}";
        return new(pos >= 0, detalle);
    }

    // Groundedness: todo precio que aparece en la respuesta tiene que existir
    // en el contexto recuperado (por VALOR, no por texto idéntico: "$1.500,00"
    // y "$1500.00" son el mismo precio y el modelo puede escribir cualquiera
    // de los dos formatos).
    //
    // Los precios se buscan de DOS formas: con el signo ("$1500.00") y SIN
    // el signo ("1500 pesos"). Con sólo el signo, una alucinación del tipo
    // "1500 pesos" pasaba inadvertida: el regex no veía ningún precio y el
    // caso daba verde.
    private static Veredicto VerificarGrounded(Caso c, Corrida corrida)
    {
        var contexto = string.Join(" ", corrida.Contexto.Count > 0
            ? corrida.Contexto
            : corrida.Hits.Select(h => h.Texto));
        var precios = Precios(corrida.Respuesta);
        var delContexto = Precios(contexto);
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

    // Un precio: con el signo ("$1.500,00") o sin el signo ("1500 pesos").
    private static readonly Regex RegexPrecioConSigno = new(@"\$[\d.,]+", RegexOptions.Compiled);
    private static readonly Regex RegexPrecioSinSigno =
        new(@"\d[\d.,]*\s*pesos?\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static List<string> Precios(string texto)
    {
        var lista = new List<string>();
        foreach (Match m in RegexPrecioConSigno.Matches(texto)) lista.Add(m.Value);
        foreach (Match m in RegexPrecioSinSigno.Matches(texto))
            if (!lista.Contains(m.Value, StringComparer.OrdinalIgnoreCase)) lista.Add(m.Value);
        return lista.Distinct(StringComparer.Ordinal).ToList();
    }

// El precio canónico del contexto es "$1500.00" (InvariantCulture). El
    // modelo puede escribir "$1500", "$1.500" o "$1.500,00": son el mismo
    // número y el groundedness se decide por valor. También puede escribir
    // "1500 pesos" (sin el signo): la palabra de la moneda se saca antes de
    // parsear.
    private static decimal? ValorPrecio(string s)
    {
        var t = s.Replace("$", "").Replace(" ", "").Replace(" ", "")
                 .Replace("pesos", "").Replace("peso", "").Trim();
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

    // La REGLA DEL ECO: una prohibición aparece LITERALMENTE en la pregunta
    // del caso → no se chequea (sería un eco del usuario, no una fuga).
    //
    // LITERAL, sin normalizar. Antes se comparaba la forma normalizada (sin
    // "$", sin ".00"), y eso era un PUNTO CIEGO: la pregunta de A5 dice
    // "consulta de 1500", así que la forma normalizada de "$1500" ("1500")
    // estaba en la pregunta y la prohibición se saltaba. El texto canónico del
    // otro tenant ("Consulta 560df8: $1500.00 ARS, 30 min. Consulta médica")
    // contiene "$1500.00" → con la forma normalizada era un eco y PASABA: el
    // detector era ciego justo al string que fue escrito para cazar.
    //
    // Con la comparación literal:
    //   - A5: la pregunta dice "1500", no "$1500" ⇒ "$1500" y "1500.00" siguen
    //     vivos ⇒ la fuga canónica se detecta;
    //   - A1: la pregunta sí dice "560df8" ⇒ esa prohibición sigue saltándose;
    //   - I3: la pregunta sí dice "Consultas" ⇒ "Consulta" sigue saltándose.
    private static bool EsEco(string pregunta, string prohibicion) =>
        prohibicion.Length > 0 &&
        pregunta.Contains(prohibicion, StringComparison.OrdinalIgnoreCase);


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
