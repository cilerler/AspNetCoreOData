//-----------------------------------------------------------------------------
// <copyright file="ODataResourceSetSerializer.cs" company=".NET Foundation">
//      Copyright (c) .NET Foundation and Contributors. All rights reserved.
//      See License.txt in the project root for license information.
// </copyright>
//------------------------------------------------------------------------------

using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics.Contracts;
using System.Runtime.Serialization;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http.Extensions;
using Microsoft.AspNetCore.OData.Abstracts;
using Microsoft.AspNetCore.OData.Common;
using Microsoft.AspNetCore.OData.Edm;
using Microsoft.AspNetCore.OData.Extensions;
using Microsoft.AspNetCore.OData.Formatter.Value;
using Microsoft.AspNetCore.OData.Query;
using Microsoft.AspNetCore.OData.Query.Container;
using Microsoft.AspNetCore.OData.Results;
using Microsoft.AspNetCore.OData.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.OData;
using Microsoft.OData.Edm;
using Microsoft.OData.ModelBuilder.Capabilities.V1;
using Microsoft.OData.UriParser;

namespace Microsoft.AspNetCore.OData.Formatter.Serialization
{
    /// <summary>
    /// OData serializer for serializing a collection of <see cref="IEdmEntityType" /> or <see cref="IEdmComplexType"/>
    /// </summary>
    public class ODataResourceSetSerializer : ODataEdmTypeSerializer
    {
        private const string ResourceSet = "ResourceSet";

        /// <summary>
        /// Initializes a new instance of <see cref="ODataResourceSetSerializer"/>.
        /// </summary>
        /// <param name="serializerProvider">The <see cref="IODataSerializerProvider"/> to use to write nested entries.</param>
        public ODataResourceSetSerializer(IODataSerializerProvider serializerProvider)
            : base(ODataPayloadKind.ResourceSet, serializerProvider)
        {
        }

        /// <inheritdoc />
        public override async Task WriteObjectAsync(object graph, Type type, ODataMessageWriter messageWriter, ODataSerializerContext writeContext)
        {
            if (messageWriter == null)
            {
                throw Error.ArgumentNull(nameof(messageWriter));
            }

            if (writeContext == null)
            {
                throw Error.ArgumentNull(nameof(writeContext));
            }

            IEdmEntitySetBase entitySet = writeContext.NavigationSource as IEdmEntitySetBase;

            IEdmTypeReference resourceSetType;
            if (writeContext.Path.IsUntypedPropertyPath())
            {
                resourceSetType = writeContext.TryGetEdmType(graph, type);
            }
            else
            {
                resourceSetType = writeContext.GetEdmType(graph, type);
            }
            Contract.Assert(resourceSetType != null);

            IEdmStructuredTypeReference resourceType = GetResourceType(resourceSetType);
            
            ODataWriter writer = await messageWriter.CreateODataResourceSetWriterAsync(entitySet, resourceType.StructuredDefinition())
                .ConfigureAwait(false);
            await WriteObjectInlineAsync(graph, resourceSetType, writer, writeContext)
                .ConfigureAwait(false);
        }

        /// <inheritdoc />
        public override async Task WriteObjectInlineAsync(object graph, IEdmTypeReference expectedType, ODataWriter writer,
            ODataSerializerContext writeContext)
        {
            if (writer == null)
            {
                throw Error.ArgumentNull(nameof(writer));
            }

            if (writeContext == null)
            {
                throw Error.ArgumentNull(nameof(writeContext));
            }

            if (expectedType == null)
            {
                throw Error.ArgumentNull(nameof(expectedType));
            }

            if (graph == null)
            {
                throw new SerializationException(Error.Format(SRResources.CannotSerializerNull, ResourceSet));
            }

            IEnumerable enumerable = graph as IEnumerable; // Data to serialize
            if (enumerable == null)
            {
                throw new SerializationException(
                    Error.Format(SRResources.CannotWriteType, GetType().Name, graph.GetType().FullName));
            }

            await WriteResourceSetAsync(enumerable, expectedType, writer, writeContext).ConfigureAwait(false);
        }

        private async Task WriteResourceSetAsync(IEnumerable enumerable, IEdmTypeReference resourceSetType, ODataWriter writer,
            ODataSerializerContext writeContext)
        {
            Contract.Assert(writer != null);
            Contract.Assert(writeContext != null);
            Contract.Assert(enumerable != null);
            Contract.Assert(resourceSetType != null);

            IEdmStructuredTypeReference elementType = GetResourceType(resourceSetType);
            ODataResourceSet resourceSet = CreateResourceSet(enumerable, resourceSetType.AsCollection(), writeContext);

            Func<object, Uri> nextLinkGenerator = GetNextLinkGenerator(resourceSet, enumerable, writeContext);

            if (resourceSet == null)
            {
                throw new SerializationException(Error.Format(SRResources.CannotSerializerNull, ResourceSet));
            }

            IEdmEntitySetBase entitySet = writeContext.NavigationSource as IEdmEntitySetBase;
            if (entitySet == null)
            {
                resourceSet.SetSerializationInfo(new ODataResourceSerializationInfo
                {
                    IsFromCollection = true,
                    NavigationSourceEntityTypeName = elementType.FullName(),
                    NavigationSourceKind = EdmNavigationSourceKind.UnknownEntitySet,
                    NavigationSourceName = null
                });
            }

            IODataEdmTypeSerializer resourceSerializer = SerializerProvider.GetEdmTypeSerializer(elementType);
            if (resourceSerializer == null)
            {
                throw new SerializationException(
                    Error.Format(SRResources.TypeCannotBeSerialized, elementType.FullName()));
            }

            bool isUntypedCollection = resourceSetType.IsCollectionUntyped();

            // set the nextpagelink to null to support JSON odata.streaming.
            resourceSet.NextPageLink = null;
            await writer.WriteStartAsync(resourceSet).ConfigureAwait(false);
            object lastResource = null;
            foreach (object item in enumerable)
            {
                lastResource = item;
                if (item == null || item is NullEdmComplexObject)
                {
                    if (elementType.IsEntity())
                    {
                        throw new SerializationException(SRResources.NullElementInCollection);
                    }

                    // for null complex element, it can be serialized as "null" in the collection.
                    await writer.WriteStartAsync(resource: null).ConfigureAwait(false);
                    await writer.WriteEndAsync().ConfigureAwait(false);
                }
                else if (isUntypedCollection)
                {
                    await WriteUntypedResourceSetItemAsync(item, resourceSetType, writer, writeContext).ConfigureAwait(false);
                }
                else
                {
                    await resourceSerializer.WriteObjectInlineAsync(item, elementType, writer, writeContext).ConfigureAwait(false);
                }
            }

            // Subtle and surprising behavior: If the NextPageLink property is set before calling WriteStart(resourceSet),
            // the next page link will be written early in a manner not compatible with odata.streaming=true. Instead, if
            // the next page link is not set when calling WriteStart(resourceSet) but is instead set later on that resourceSet
            // object before calling WriteEnd(), the next page link will be written at the end, as required for
            // odata.streaming=true support.

            resourceSet.NextPageLink = nextLinkGenerator(lastResource);

            await writer.WriteEndAsync().ConfigureAwait(false);
        }

        private async Task WriteUntypedResourceSetItemAsync(object item, IEdmTypeReference parentSetType, ODataWriter writer, ODataSerializerContext writeContext)
        {
            Contract.Assert(item != null); // "item == null" is handled.

            Type itemType = item.GetType();
            IEdmTypeReference itemEdmType = writeContext.TryGetEdmType(item, itemType);

            // if the type of value is declared as enum in the edm model, let's use it.
            if (itemEdmType != null && itemEdmType.IsEnum())
            {
                await WriteEnumItemAsync(item, itemEdmType, parentSetType, writer, writeContext).ConfigureAwait(false);
                return;
            }

            // the value is an enum whose type is not defined in edm model. Let's write it as string.
            if (TypeHelper.IsEnum(item.GetType()))
            {
                await WriteEnumItemAsync(item, null, parentSetType, writer, writeContext).ConfigureAwait(false);
                return;
            }

            // The value is a primitive value, write it as untyped primitive value item.
            if (itemEdmType != null && itemEdmType.IsPrimitive())
            {
                await WritePrimitiveItemAsync(item, itemEdmType, parentSetType, writer, writeContext).ConfigureAwait(false);
                return;
            }

            if (itemEdmType != null && itemEdmType.IsCollection())
            {
                // If the value is a IList<int>, or other similars, the TryGetEdmType(...) return Collection(Edm.Int32).
                // But, ODL doesn't support to write ODataCollectionValue.
                // Let's directly use untyped collection serialization no matter what type this collection is.
                itemEdmType = EdmUntypedHelpers.NullableUntypedCollectionReference;
                await WriteResourceSetItemAsync(item, itemEdmType, parentSetType, writer, writeContext).ConfigureAwait(false);
                return;
            }

            if (itemEdmType != null && itemEdmType.IsStructuredOrUntypedStructured())
            {
                await WriteResourceItemAsync(item, itemEdmType, parentSetType, writer, writeContext).ConfigureAwait(false);
            }
        }

        /// <summary>
        /// Write a primitive value into a collection (untyped collection)
        /// </summary>
        /// <param name="primitiveValue">The primitive value.</param>
        /// <param name="primitiveType">The primitive edm type.</param>
        /// <param name="parentSetType">The parent collection edm type.</param>
        /// <param name="writer">The writer.</param>
        /// <param name="writeContext">The writer context.</param>
        /// <returns>Task.</returns>
        protected virtual async Task WritePrimitiveItemAsync(object primitiveValue, IEdmTypeReference primitiveType,
            IEdmTypeReference parentSetType,
            ODataWriter writer, ODataSerializerContext writeContext)
        {
            if (primitiveValue == null)
            {
                throw Error.ArgumentNull(nameof(primitiveValue));
            }

            var odataPrimitiveValue = ODataPrimitiveSerializer.CreatePrimitive(primitiveValue, primitiveType.AsPrimitive(), writeContext);
            await writer.WritePrimitiveAsync(odataPrimitiveValue).ConfigureAwait(false);
        }

        /// <summary>
        /// Write an enum value into a collection (untyped collection)
        /// </summary>
        /// <param name="enumValue">The enum value.</param>
        /// <param name="enumType">The enum edm type or null if no edm type defined.</param>
        /// <param name="parentSetType">The parent collection edm type.</param>
        /// <param name="writer">The writer.</param>
        /// <param name="writeContext">The writer context.</param>
        /// <returns>Task.</returns>
        protected virtual async Task WriteEnumItemAsync(object enumValue, IEdmTypeReference enumType, IEdmTypeReference parentSetType,
            ODataWriter writer, ODataSerializerContext writeContext)
        {
            IEdmTypeReference edmTypeRef = EdmCoreModel.Instance.GetString(true);
            if (enumType == null)
            {
                // we don't have the Edm enum type in the model, let's write it as string.
                string enumValueStr = enumValue.ToString();
                await WritePrimitiveItemAsync(enumValueStr, edmTypeRef, parentSetType, writer, writeContext).ConfigureAwait(false);
            }
            else
            {
                ODataEnumSerializer enumSerializer = writeContext?.Request?.GetRouteServices()?.GetRequiredService<ODataEnumSerializer>();
                if (enumSerializer != null)
                {
                    ODataEnumValue oDataEnumValue = enumSerializer.CreateODataEnumValue(enumValue, enumType.AsEnum(), writeContext);

                    // We can't write the 'ODataEnumValue' directly by calling write methods on writer.
                    // Let's switch to use 'string' and write it as primitive value.
                    // Once ODL supports, let's call the correct writing methods.
                    await WritePrimitiveItemAsync(oDataEnumValue.Value, edmTypeRef, parentSetType, writer, writeContext);
                }
            }
        }

        /// <summary>
        /// Write a nested collection value into a collection (untyped collection)
        /// </summary>
        /// <param name="itemSetValue">The collection value.</param>
        /// <param name="itemSetType">The nested collection Edm type.</param>
        /// <param name="parentSetType">The parent collection edm type.</param>
        /// <param name="writer">The writer.</param>
        /// <param name="writeContext">The writer context.</param>
        /// <returns>Task.</returns>
        protected virtual async Task WriteResourceSetItemAsync(object itemSetValue, IEdmTypeReference itemSetType, IEdmTypeReference parentSetType,
            ODataWriter writer, ODataSerializerContext writeContext)
        {
            IODataEdmTypeSerializer resourceSetSerializer = SerializerProvider.GetEdmTypeSerializer(itemSetType);
            await resourceSetSerializer.WriteObjectInlineAsync(itemSetValue, itemSetType, writer, writeContext).ConfigureAwait(false);
        }

        /// <summary>
        /// Write a nested resource/object into a collection (untyped collection)
        /// </summary>
        /// <param name="resourceValue">The resource value.</param>
        /// <param name="resourceType">The nested resource Edm type.</param>
        /// <param name="parentSetType">The parent collection edm type.</param>
        /// <param name="writer">The writer.</param>
        /// <param name="writeContext">The writer context.</param>
        /// <returns>Task.</returns>
        protected virtual async Task WriteResourceItemAsync(object resourceValue, IEdmTypeReference resourceType, IEdmTypeReference parentSetType,
            ODataWriter writer, ODataSerializerContext writeContext)
        {
            IODataEdmTypeSerializer resourceSerializer = SerializerProvider.GetEdmTypeSerializer(resourceType);
            await resourceSerializer.WriteObjectInlineAsync(resourceValue, resourceType, writer, writeContext).ConfigureAwait(false);
        }

        /// <summary>
        /// Create the <see cref="ODataResourceSet"/> to be written for the given resourceSet instance.
        /// </summary>
        /// <param name="resourceSetInstance">The instance representing the resourceSet being written.</param>
        /// <param name="resourceSetType">The EDM type of the resourceSet being written.</param>
        /// <param name="writeContext">The serializer context.</param>
        /// <returns>The created <see cref="ODataResourceSet"/> object.</returns>
        public virtual ODataResourceSet CreateResourceSet(IEnumerable resourceSetInstance, IEdmCollectionTypeReference resourceSetType,
            ODataSerializerContext writeContext)
        {
            if (writeContext == null)
            {
                throw Error.ArgumentNull(nameof(writeContext));
            }

            ODataResourceSet resourceSet = new ODataResourceSet
            {
                TypeName = resourceSetType.FullName()
            };

            IEdmStructuredTypeReference structuredType = GetResourceType(resourceSetType).AsStructured();
            if (writeContext.NavigationSource != null && structuredType.IsEntity())
            {
                ResourceSetContext resourceSetContext = ResourceSetContext.Create(writeContext, resourceSetInstance);
                IEdmEntityType entityType = structuredType.AsEntity().EntityDefinition();
                var operations = writeContext.Model.GetAvailableOperationsBoundToCollection(entityType);
                var odataOperations = CreateODataOperations(operations, resourceSetContext, writeContext);
                foreach (var odataOperation in odataOperations)
                {
                    ODataAction action = odataOperation as ODataAction;
                    if (action != null)
                    {
                        resourceSet.AddAction(action);
                    }
                    else
                    {
                        resourceSet.AddFunction((ODataFunction)odataOperation);
                    }
                }
            }

            if (writeContext.ExpandedResource == null)
            {
                // If we have more OData format specific information apply it now, only if we are the root feed.
                PageResult odataResourceSetAnnotations = resourceSetInstance as PageResult;
                if (odataResourceSetAnnotations != null)
                {
                    resourceSet.Count = odataResourceSetAnnotations.Count;
                    resourceSet.NextPageLink = odataResourceSetAnnotations.NextPageLink;
                }
                else if (writeContext.Request != null)
                {
                    IODataFeature odataFeature = writeContext.Request.ODataFeature();
                    resourceSet.NextPageLink = odataFeature.NextLink;
                    resourceSet.DeltaLink = odataFeature.DeltaLink;

                    long? countValue = odataFeature.TotalCount;
                    if (countValue.HasValue)
                    {
                        resourceSet.Count = countValue.Value;
                    }
                }
            }
            else
            {
                ICountOptionCollection countOptionCollection = resourceSetInstance as ICountOptionCollection;
                if (countOptionCollection != null && countOptionCollection.TotalCount != null)
                {
                    resourceSet.Count = countOptionCollection.TotalCount;
                }
            }

            return resourceSet;
        }

        /// <summary>
        /// Creates a function that takes in an object and generates nextlink uri.
        /// </summary>
        /// <param name="resourceSet">The resource set describing a collection of structured objects.</param>
        /// <param name="resourceSetInstance">The instance representing the resourceSet being written.</param>
        /// <param name="writeContext">The serializer context.</param>
        /// <returns>The function that generates the NextLink from an object.</returns>
        internal static Func<object, Uri> GetNextLinkGenerator(ODataResourceSetBase resourceSet, IEnumerable resourceSetInstance, ODataSerializerContext writeContext)
        {
            if (resourceSet != null && resourceSet.NextPageLink != null)
            {
                Uri defaultUri = resourceSet.NextPageLink;
                return (obj) => { return defaultUri; };
            }

            if (writeContext.ExpandedResource == null)
            {
                if (writeContext.Request != null && writeContext.QueryContext != null)
                {
                    SkipTokenHandler handler = writeContext.QueryContext.GetSkipTokenHandler();
                    return (obj) => { return handler.GenerateNextPageLink(new System.Uri(writeContext.Request.GetEncodedUrl()),
                        (writeContext.Request.ODataFeature() as ODataFeature).PageSize, obj, writeContext); };
                }
            }
            else
            {
                // nested resourceSet
                ITruncatedCollection truncatedCollection = resourceSetInstance as ITruncatedCollection;
                if (truncatedCollection != null && truncatedCollection.IsTruncated)
                {
                    return (obj) => { return GetNestedNextPageLink(writeContext, truncatedCollection.PageSize, obj); };
                }
            }

            return (obj) => { return null; };
        }

        /// <summary>
        ///  Creates an <see cref="ODataOperation" /> to be written for the given operation and the resourceSet instance.
        /// </summary>
        /// <param name="operation">The OData operation.</param>
        /// <param name="resourceSetContext">The context for the resourceSet instance being written.</param>
        /// <param name="writeContext">The serializer context.</param>
        /// <returns>The created operation or null if the operation should not be written.</returns>
        public virtual ODataOperation CreateODataOperation(IEdmOperation operation, ResourceSetContext resourceSetContext, ODataSerializerContext writeContext)
        {
            if (operation == null)
            {
                throw Error.ArgumentNull(nameof(operation));
            }

            if (resourceSetContext == null)
            {
                throw Error.ArgumentNull(nameof(resourceSetContext));
            }

            if (writeContext == null)
            {
                throw Error.ArgumentNull(nameof(writeContext));
            }

            ODataMetadataLevel metadataLevel = writeContext.MetadataLevel;
            IEdmModel model = writeContext.Model;

            if (metadataLevel != ODataMetadataLevel.Full)
            {
                return null;
            }

            OperationLinkBuilder builder = model.GetOperationLinkBuilder(operation);
            if (builder == null)
            {
                return null;
            }

            Uri target = builder.BuildLink(resourceSetContext);
            if (target == null)
            {
                return null;
            }

            Uri baseUri = new Uri(writeContext.Request.CreateODataLink(MetadataSegment.Instance));
            Uri metadata = new Uri(baseUri, "#" + operation.FullName());

            ODataOperation odataOperation;
            IEdmAction action = operation as IEdmAction;
            if (action != null)
            {
                odataOperation = new ODataAction();
            }
            else
            {
                odataOperation = new ODataFunction();
            }
            odataOperation.Metadata = metadata;

            // Always omit the title in minimal/no metadata modes.
            ODataResourceSerializer.EmitTitle(model, operation, odataOperation);

            // Omit the target in minimal/no metadata modes unless it doesn't follow conventions.
            if (metadataLevel == ODataMetadataLevel.Full || !builder.FollowsConventions)
            {
                odataOperation.Target = target;
            }

            return odataOperation;
        }

        private IEnumerable<ODataOperation> CreateODataOperations(IEnumerable<IEdmOperation> operations, ResourceSetContext resourceSetContext, ODataSerializerContext writeContext)
        {
            Contract.Assert(operations != null);
            Contract.Assert(resourceSetContext != null);
            Contract.Assert(writeContext != null);

            foreach (IEdmOperation operation in operations)
            {
                ODataOperation oDataOperation = CreateODataOperation(operation, resourceSetContext, writeContext);
                if (oDataOperation != null)
                {
                    yield return oDataOperation;
                }
            }
        }

        private static Uri GetNestedNextPageLink(ODataSerializerContext writeContext, int pageSize, object obj)
        {
            Contract.Assert(writeContext.ExpandedResource != null);
            Uri navigationLink;
            IEdmNavigationSource sourceNavigationSource = writeContext.ExpandedResource.NavigationSource;
            NavigationSourceLinkBuilderAnnotation linkBuilder = writeContext.Model.GetNavigationSourceLinkBuilder(sourceNavigationSource);

            // In Contained Navigation, we don't have navigation property binding,
            // Hence we cannot get the NavigationLink from the NavigationLinkBuilder
            if (writeContext.NavigationSource.NavigationSourceKind() == EdmNavigationSourceKind.ContainedEntitySet)
            {
                // Contained navigation.
                Uri idlink = linkBuilder.BuildIdLink(writeContext.ExpandedResource);

                var link = idlink.ToString() + "/" + writeContext.NavigationProperty.Name;
                navigationLink = new Uri(link);
            }
            else
            {
                // Non-Contained navigation.
                navigationLink =
                    linkBuilder.BuildNavigationLink(writeContext.ExpandedResource, writeContext.NavigationProperty);
            }

            Uri nestedNextLink = GenerateQueryFromExpandedItem(writeContext, navigationLink);

            SkipTokenHandler nextLinkGenerator = null;
            if (writeContext.QueryContext != null)
            {
                nextLinkGenerator = writeContext.QueryContext.GetSkipTokenHandler();
            }

            if (nestedNextLink != null)
            {
                if (nextLinkGenerator != null)
                {
                    return nextLinkGenerator.GenerateNextPageLink(nestedNextLink, pageSize, obj, writeContext);
                }

                return GetNextPageHelper.GetNextPageLink(nestedNextLink, pageSize);
            }

            return null;
        }

        private static Uri GenerateQueryFromExpandedItem(ODataSerializerContext writeContext, Uri navigationLink)
        {
            string serviceRoot = writeContext.Request.CreateODataLink(new List<ODataPathSegment>());
            Uri serviceRootUri = new Uri(serviceRoot);
            ODataUriParser parser = new ODataUriParser(writeContext.Model, serviceRootUri, navigationLink);
            ODataUri newUri = parser.ParseUri();
            newUri.SelectAndExpand = writeContext.SelectExpandClause;
            if (writeContext.CurrentExpandedSelectItem != null)
            {
                newUri.OrderBy = writeContext.CurrentExpandedSelectItem.OrderByOption;
                newUri.Filter = writeContext.CurrentExpandedSelectItem.FilterOption;
                newUri.Skip = writeContext.CurrentExpandedSelectItem.SkipOption;
                newUri.Top = writeContext.CurrentExpandedSelectItem.TopOption;

                if (writeContext.CurrentExpandedSelectItem.CountOption != null)
                {
                    if (writeContext.CurrentExpandedSelectItem.CountOption.HasValue)
                    {
                        newUri.QueryCount = writeContext.CurrentExpandedSelectItem.CountOption.Value;
                    }
                }

                ExpandedNavigationSelectItem expandedNavigationItem = writeContext.CurrentExpandedSelectItem as ExpandedNavigationSelectItem;
                if (expandedNavigationItem != null)
                {
                    newUri.SelectAndExpand = expandedNavigationItem.SelectAndExpand;
                }
            }

            ODataUrlKeyDelimiter keyDelimiter = ODataUrlKeyDelimiter.Parentheses;
            ODataOptions options = writeContext.Request.HttpContext.RequestServices.GetRequiredService<IOptions<ODataOptions>>().Value;
            if (options != null)
            {
                keyDelimiter = options.UrlKeyDelimiter;
            }

            return newUri.BuildUri(keyDelimiter);
        }

        private static IEdmStructuredTypeReference GetResourceType(IEdmTypeReference resourceSetType)
        {
            if (resourceSetType.IsStructuredOrUntypedStructuredCollection())
            {
                IEdmTypeReference elementType = resourceSetType.AsCollection().ElementType();
                return elementType.ToStructuredTypeReference();
            }

            string message = Error.Format(SRResources.CannotWriteType, typeof(ODataResourceSetSerializer).Name, resourceSetType.FullName());
            throw new SerializationException(message);
        }
    }
}
