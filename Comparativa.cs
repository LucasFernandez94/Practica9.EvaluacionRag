using System.Globalization;
using System.Text;

namespace Practica9.EvaluacionRag;

// Los 3 experimentos: el MISMO set de casos corrido de 3 formas.
//   E1 modelos  → cada modelo local del barrido contra el MISMO set de casos
//   E2 prompts  → el de la P8 vs una variante estricta
//   E3 k        → 1, 3 y 6 (SQL y contexto por separado)
public static class Comparativa
{
    public static readonly string PromptEstricto =
        "Regla extra: si la pregunta pide un servicio que NO está en el contexto, decí únicamente " +
        "\"No tengo esa información.\" y no menciones servicios parecidos ni sus precios.";

    public static IReadOnlyList<Escenario> Escenarios() => new[]
    {
        // E1: el MISMO set de casos contra cada modelo local. De más chico a más grande:
        // el objetivo es decidir con números cuáles 2 modelos quedan para las prácticas.
        // (2026-10-27: granite4.1 re-agregado a las pruebas.)
        new Escenario("E1-granite4.1-3b", "granite4.1:3b"),
        new Escenario("E1-llama32-3b", "llama3.2:3b"),
        new Escenario("E1-qwen3.5-4b", "qwen3.5:4b"),
        new Escenario("E2-prompt-p8", "llama3.2:3b"),
        new Escenario("E2-prompt-estricto", "llama3.2:3b", PromptEstricto),
        new Escenario("E3-k1", "llama3.2:3b", ForzarK: 1, ForzarContextoK: 1),
        new Escenario("E3-k3", "llama3.2:3b", ForzarK: 3, ForzarContextoK: 3),
        new Escenario("E3-k6", "llama3.2:3b", ForzarK: 6, ForzarContextoK: 6),
    };

    public static async Task<IReadOnlyList<Resumen>> CorrerTodo(
        IReadOnlyList<Caso> casos, Entorno env, string salidaBase)
    {
        var res = new List<Resumen>();
        foreach (var esc in Escenarios())
            res.Add(await Runner.CorrerAsync(casos, esc, env, salidaBase));
        Escribir(res, salidaBase);
        return res;
    }

    public static void Escribir(IReadOnlyList<Resumen> resúmenes, string salidaBase)
    {
        var sb = new StringBuilder();
        // Los p50/p95 por etapa (spec §5.2): embed, retrieval (el SQL completo),
        // rank (el SELECT solo) y generación. Son por ESCENARIO (el mismo
        // reloj para todos los grupos): la fila es (escenario × grupo).
        sb.AppendLine("escenario,modelo,grupo,pass,total,tasa,seg_promedio,seg_p90,casos_no_info,frases_exactas,tasa_frase_exacta," +
                      "embed_p50,embed_p95,retrieval_p50,retrieval_p95,rank_p50,rank_p95,gen_p50,gen_p95");
        foreach (var r in resúmenes)
        {
            if (r.PorGrupo.Count == 0) continue;
            var embed = r.Etapa("embed");
            var retrieval = r.Etapa("retrieval");
            var rank = r.Etapa("rank");
            var gen = r.Etapa("generacion");
            foreach (var g in r.PorGrupo.OrderBy(g => g.Key))
                sb.AppendLine(string.Join(",",
                    r.Escenario, r.Modelo, g.Key, g.Value.Pass, g.Value.Total,
                    g.Value.Total == 0 ? "0" : ((double)g.Value.Pass / g.Value.Total).ToString("F3", CultureInfo.InvariantCulture),
                    r.SegPromedio.ToString("F2", CultureInfo.InvariantCulture),
                    r.SegP90.ToString("F2", CultureInfo.InvariantCulture),
                    g.Value.CasosNoInfo, g.Value.FrasesExactas,
                    g.Value.CasosNoInfo == 0 ? "" : ((double)g.Value.FrasesExactas / g.Value.CasosNoInfo).ToString("F3", CultureInfo.InvariantCulture),
                    embed?.P50 ?? 0, embed?.P95 ?? 0,
                    retrieval?.P50 ?? 0, retrieval?.P95 ?? 0,
                    rank?.P50 ?? 0, rank?.P95 ?? 0,
                    gen?.P50 ?? 0, gen?.P95 ?? 0));
        }
        File.WriteAllText($"{salidaBase}-comparativa.csv", sb.ToString());

        Console.WriteLine("\n===== Comparativa =====");
        Console.WriteLine($"  {"escenario",-22} {"pass",-9} {"tasa",-7} {"p90",-6} {"exactas",-8} por grupo");
        foreach (var r in resúmenes)
        {
            var grupos = string.Join(" ", r.PorGrupo.OrderBy(g => g.Key)
                .Select(g => $"{g.Key}={g.Value.Pass}/{g.Value.Total}"));
            var exactas = r.CasosNoInfo == 0 ? "n/a" : $"{r.FrasesExactas}/{r.CasosNoInfo}";
            Console.WriteLine($"  {r.Escenario,-22} {r.Pass + "/" + r.Total,-9} {r.Tasa,-7:P0} {r.SegP90,-6:F1} {exactas,-8} {grupos}");
        }
        Console.WriteLine("\n  exactas = casos que respondieron con la frase LITERAL del prompt");
        Console.WriteLine("  (\"No tengo esa información.\"), de los que piden fail-closed. No es un pass/fail.");

        // Las etapas por escenario: p50/p95 de embed, retrieval, rank y
        // generación (spec §5.2).
        Console.WriteLine("\n  etapas (p50/p95 ms) por escenario:");
        Console.WriteLine($"  {"escenario",-22} {"embed",-10} {"retrieval",-10} {"rank",-10} {"generacion",-10}");
        foreach (var r in resúmenes)
        {
            if (r.Etapas.Count == 0) continue;
            var celda = (string nombre) =>
            {
                var e = r.Etapa(nombre);
                return e is null ? "n/a" : $"{e.P50}/{e.P95}";
            };
            Console.WriteLine($"  {r.Escenario,-22} {celda("embed"),-10} {celda("retrieval"),-10} " +
                              $"{celda("rank"),-10} {celda("generacion"),-10}");
        }
        Console.WriteLine("  retrieval = conexión + count(*) + ranking · rank = sólo el SELECT de ranking");
    }
}
