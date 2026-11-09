using System;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using Microsoft.CodeAnalysis;

namespace UnionNarrowing.Suppressors.Analysis;

/// <summary>
/// Reads fields of a type from another assembly straight from its metadata.
/// A reference only imports what other assemblies can see, so private fields (an auto-property's backing field, say) don't show up as symbols.
/// </summary>
internal static class MetadataFields
{
    private static bool Visit(INamedTypeSymbol type, Action<MetadataReader, FieldDefinition> visit)
    {
        type = type.OriginalDefinition;

        if (type.MetadataToken == 0 || type.ContainingModule?.GetMetadata() is not { } module)
        {
            return false;
        }

        try
        {
            var reader = module.GetMetadataReader();
            var definition = reader.GetTypeDefinition((TypeDefinitionHandle)MetadataTokens.EntityHandle(type.MetadataToken));

            foreach (var handle in definition.GetFields())
            {
                visit(reader, reader.GetFieldDefinition(handle));
            }

            return true;
        }
        catch (Exception e) when (e is BadImageFormatException or InvalidCastException)
        {
            return false;
        }
    }

    /// <summary>
    /// Whether every instance field of the type is <c>initonly</c>.
    /// False if the metadata can't be read.
    /// </summary>
    public static bool AllInstanceFieldsReadonly(INamedTypeSymbol type)
    {
        var all = true;
        return
            Visit(type, (_, field) =>
            {
                if ((field.Attributes & FieldAttributes.Static) == 0 && (field.Attributes & FieldAttributes.InitOnly) == 0)
                {
                    all = false;
                }
            })
            && all;
    }

    /// <summary>
    /// Whether the field with this name is <c>initonly</c>, or null if there's no such field (or the metadata can't be read).
    /// </summary>
    public static bool? IsInitOnly(INamedTypeSymbol type, string fieldName)
    {
        bool? found = null;
        
        Visit(type, (reader, field) =>
        {
            if (reader.StringComparer.Equals(field.Name, fieldName))
            {
                found = (field.Attributes & FieldAttributes.InitOnly) != 0;
            }
        });

        return found;
    }
}
