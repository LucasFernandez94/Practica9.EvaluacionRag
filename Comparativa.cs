using System.Globalization;
using System.Text;

namespace Practica9.EvaluacionRag;

// Los 3 escenarios: el MISMO set de casos corrido de 3 formas.
//   E1 modelos  → llama3.2:3b vs qwen2.5:1.5b
//   E2 prompts  → el de la P8 vs una variante estricta
//   E3 k        → 1, 3 y 6 (SQL y contexto por separado)
public static class Comparativa
{
    public static readonly string PromptEstricto =
        "Regla extra: si la pregunta pide un servicio que NO está en el contexto, decí únicamente " +
        "\"No tengo esa información.\" y no menciones servicios parecidos ni sus precios.";

    public static IReadOnlyList<Escenario> Escenarios() => new[]
    {
        new Escenario("E1-llama32", "llama3.2:3b"),
        new Escenario("E1-qwen25", "qwen2.5:1.5b"),
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
        sb.AppendLine("escenario,modelo,grupo,pass,total,tasa,seg_promedio,seg_p90,casos_no_info,frases_exactas,tasa_frase_exacta");
        foreach (var r in resúmenes)
        {
            if (r.PorGrupo.Count == 0) continue;
            foreach (var g in r.PorGrupo.OrderBy(g => g.Key))
                sb.AppendLine(string.Join(",",
                    r.Escenario, r.Modelo, g.Key, g.Value.Pass, g.Value.Total,
                    g.Value.Total == 0 ? "0" : ((double)g.Value.Pass / g.Value.Total).ToString("F3", CultureInfo.InvariantCulture),
                    r.SegPromedio.ToString("F2", CultureInfo.InvariantCulture),
                    r.SegP90.ToString("F2", CultureInfo.InvariantCulture),
                    g.Value.CasosNoInfo, g.Value.FrasesExactas,
                    g.Value.CasosNoInfo == 0 ? "" : ((double)g.Value.FrasesExactas / g.Value.CasosNoInfo).ToString("F3", CultureInfo.InvariantCulture)));
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
    }
}
