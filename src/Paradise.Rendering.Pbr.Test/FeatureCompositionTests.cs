using System.Reflection;
using Paradise.Rendering.Graph;

namespace Paradise.Rendering.Pbr.Test;

public class FeatureCompositionTests
{
    [Test]
    public async Task built_in_features_share_results_without_retaining_or_accepting_sibling_features()
    {
        var dependencies = new List<string>();
        var features = typeof(PbrRenderer).Assembly.GetTypes()
            .Where(type => type.IsClass && typeof(IRenderFeature).IsAssignableFrom(type));
        const BindingFlags Members = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
        foreach (var feature in features)
        {
            foreach (var field in feature.GetFields(Members | BindingFlags.Static))
                if (ContainsSiblingFeature(field.FieldType, feature))
                    dependencies.Add($"{feature.Name}.{field.Name}: {field.FieldType}");
            foreach (var constructor in feature.GetConstructors(Members))
                foreach (var parameter in constructor.GetParameters())
                    if (ContainsSiblingFeature(parameter.ParameterType, feature))
                        dependencies.Add($"{feature.Name} constructor parameter {parameter.Name}: {parameter.ParameterType}");
        }

        await Assert.That(dependencies).IsEmpty();
    }

    private static bool ContainsSiblingFeature(Type type, Type owner)
    {
        if (type == owner) return false;
        if (typeof(IRenderFeature).IsAssignableFrom(type)) return true;
        if (type.HasElementType && ContainsSiblingFeature(type.GetElementType()!, owner)) return true;
        return type.IsGenericType && type.GetGenericArguments().Any(argument => ContainsSiblingFeature(argument, owner));
    }
}
