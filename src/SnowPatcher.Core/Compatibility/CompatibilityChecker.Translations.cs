using System.Text;
using System.Text.Json;
using SnowPatcher.Core.Packages;

namespace SnowPatcher.Core.Compatibility;

public static partial class CompatibilityChecker
{
    private static void ApplyOptionalTranslations(ResourceView view, List<Result> ordered, List<Claim> claims,
        List<CheckedOperation> operations, CancellationToken token)
    {
        // Optional output never feeds required guards or dependency replay.
        foreach (var result in ordered)
        {
            foreach (var operation in result.Package!.Manifest.Operations.Where(o => o.IsOptional))
            {
                token.ThrowIfCancellationRequested();
                var target = operation.Target;
                var description = ResourceView.Describe(target);
                try
                {
                    var overlap = claims.FirstOrDefault(c => c.Kind != "string" &&
                        (ResourceView.IsAncestor(c.Target, target) || ResourceView.IsAncestor(target, c.Target) ||
                         PackagePath.TargetKey(c.Target) == PackagePath.TargetKey(target)));
                    if (overlap is not null)
                    {
                        throw new CheckFailure("conflict", $"Overlaps a required operation or check in {overlap.Mod.Name} ({overlap.Operation}).");
                    }

                    Apply(view, result, operation, true, null, ordered.Count, token);
                    operations.Add(new CheckedOperation(result.Id, operation.Id, operation.Type, description));
                }
                catch (Exception error) when (error is CheckFailure { Code: "changed" or "missing" or "unsupported" or "conflict" } or JsonException or DecoderFallbackException)
                {
                    var code = error is CheckFailure failure ? failure.Code : "unsupported";
                    result.Issues.Add(new CompatibilityIssue(code, "Optional translation skipped. " + error.Message,
                        operation.Id, description, IsBlocking: false));
                }
            }
        }
    }
}
