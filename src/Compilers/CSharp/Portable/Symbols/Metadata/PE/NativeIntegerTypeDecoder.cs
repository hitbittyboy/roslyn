﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

#nullable enable

using System;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Reflection.Metadata;
using Microsoft.CodeAnalysis.PooledObjects;

namespace Microsoft.CodeAnalysis.CSharp.Symbols.Metadata.PE
{
    internal struct NativeIntegerTypeDecoder
    {
        private class ErrorTypeException : Exception { }

        internal static TypeSymbol TransformType(TypeSymbol type, EntityHandle handle, PEModuleSymbol containingModule)
        {
            return containingModule.Module.HasNativeIntegerAttribute(handle, out var transformFlags) ?
                TransformType(type, transformFlags) :
                type;
        }

        private static TypeSymbol TransformType(TypeSymbol type, ImmutableArray<bool> transformFlags)
        {
            var decoder = new NativeIntegerTypeDecoder(transformFlags);
            try
            {
                var result = decoder.TransformType(type);
                if (decoder._index == transformFlags.Length)
                {
                    return result;
                }
                else
                {
                    return new UnsupportedMetadataTypeSymbol();
                }
            }
            catch (UnsupportedSignatureContent)
            {
                return new UnsupportedMetadataTypeSymbol();
            }
            catch (ErrorTypeException)
            {
                // If we failed to decode because there was an error type involved, marking the
                // metadata as unsupported means that we'll cover up the error that would otherwise
                // be reported for the type. This would likely lead to a worse error message as we
                // would just report a BindToBogus, so return the type unchanged.
                Debug.Assert(type.ContainsErrorType());
                return type;
            }
        }

        private readonly ImmutableArray<bool> _transformFlags;
        private int _index;

        private NativeIntegerTypeDecoder(ImmutableArray<bool> transformFlags)
        {
            _transformFlags = transformFlags;
            _index = 0;
        }

        private TypeWithAnnotations TransformTypeWithAnnotations(TypeWithAnnotations type)
        {
            return type.WithTypeAndModifiers(TransformType(type.Type), type.CustomModifiers);
        }

        private TypeSymbol TransformType(TypeSymbol type)
        {
            switch (type.TypeKind)
            {
                case TypeKind.Array:
                    return TransformArrayType((ArrayTypeSymbol)type);
                case TypeKind.Pointer:
                    return TransformPointerType((PointerTypeSymbol)type);
                case TypeKind.TypeParameter:
                case TypeKind.Dynamic:
                    IgnoreIndex();
                    return type;
                case TypeKind.Class:
                case TypeKind.Struct:
                case TypeKind.Interface:
                case TypeKind.Delegate:
                case TypeKind.Enum:
                    return TransformNamedType((NamedTypeSymbol)type);
                default:
                    Debug.Assert(type.TypeKind == TypeKind.Error);
                    throw new ErrorTypeException();
            }
        }

        private NamedTypeSymbol TransformNamedType(NamedTypeSymbol type)
        {
            int index = Increment();

            if (!type.IsGenericType)
            {
                return _transformFlags[index] ? TransformTypeDefinition(type) : type;
            }

            if (_transformFlags[index])
            {
                throw new UnsupportedSignatureContent();
            }

            var allTypeArguments = ArrayBuilder<TypeWithAnnotations>.GetInstance();
            type.GetAllTypeArgumentsNoUseSiteDiagnostics(allTypeArguments);

            bool haveChanges = false;
            for (int i = 0; i < allTypeArguments.Count; i++)
            {
                TypeWithAnnotations oldTypeArgument = allTypeArguments[i];
                TypeWithAnnotations newTypeArgument = TransformTypeWithAnnotations(oldTypeArgument);
                if (!oldTypeArgument.IsSameAs(newTypeArgument))
                {
                    allTypeArguments[i] = newTypeArgument;
                    haveChanges = true;
                }
            }

            NamedTypeSymbol result = haveChanges ? type.WithTypeArguments(allTypeArguments.ToImmutable()) : type;
            allTypeArguments.Free();
            return result;
        }

        private ArrayTypeSymbol TransformArrayType(ArrayTypeSymbol type)
        {
            IgnoreIndex();
            return type.WithElementType(TransformTypeWithAnnotations(type.ElementTypeWithAnnotations));
        }

        private PointerTypeSymbol TransformPointerType(PointerTypeSymbol type)
        {
            IgnoreIndex();
            return type.WithPointedAtType(TransformTypeWithAnnotations(type.PointedAtTypeWithAnnotations));
        }

        private int Increment()
        {
            if (_index < _transformFlags.Length)
            {
                return _index++;
            }
            throw new UnsupportedSignatureContent();
        }

        private void IgnoreIndex()
        {
            var index = Increment();
            if (_transformFlags[index])
            {
                throw new UnsupportedSignatureContent();
            }
        }

        private static NamedTypeSymbol TransformTypeDefinition(NamedTypeSymbol type)
        {
            switch (type.SpecialType)
            {
                case SpecialType.System_IntPtr:
                case SpecialType.System_UIntPtr:
                    return type.AsNativeInteger();
                default:
                    throw new UnsupportedSignatureContent();
            }
        }
    }
}
