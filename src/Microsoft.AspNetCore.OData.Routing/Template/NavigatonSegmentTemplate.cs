﻿// Copyright (c) Microsoft Corporation.  All rights reserved.
// Licensed under the MIT License.  See License.txt in the project root for license information.

using System;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.OData.Edm;
using Microsoft.OData.UriParser;

namespace Microsoft.AspNetCore.OData.Routing.Template
{
    /// <summary>
    /// Represents a template that could match an <see cref="IEdmNavigationProperty"/>.
    /// </summary>
    public class NavigationSegmentTemplate : ODataSegmentTemplate
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="NavigationSegmentTemplate" /> class.
        /// </summary>
        /// <param name="navigation">The Edm navigation property.</param>
        public NavigationSegmentTemplate(IEdmNavigationProperty navigation)
        {
            Navigation = navigation ?? throw new ArgumentNullException(nameof(navigation));
        }

        /// <inheritdoc />
        public override string Template => Navigation.Name;

        /// <summary>
        /// Gets the wrapped navigation property.
        /// </summary>
        public IEdmNavigationProperty Navigation { get; }

        /// <inheritdoc />
        public override ODataPathSegment GenerateODataSegment(IEdmModel model, IEdmNavigationSource previous,
            RouteValueDictionary routeValue, QueryString queryString)
        {
            // TODO: calculate the target
            return new NavigationPropertySegment(Navigation, previous);
        }
    }
}
