﻿using System;
using QueryBuilder.Edm;
using Microsoft.OData.Edm;
using Microsoft.OData.UriParser;

namespace QueryBuilder.Query.Validator
{
    /// <summary>
    /// Represents a validator used to validate a <see cref="CountQueryOption"/> 
    /// based on the <see cref="ODataValidationSettings"/>.
    /// </summary>
    public class CountQueryValidator : ICountQueryValidator
    {
        /// <summary>
        /// Validates a <see cref="CountQueryOption" />.
        /// </summary>
        /// <param name="countQueryOption">The $count query.</param>
        /// <param name="validationSettings">The validation settings.</param>
        public virtual void Validate(CountQueryOption countQueryOption, ODataValidationSettings validationSettings)
        {
            if (countQueryOption == null)
            {
                throw Error.ArgumentNull(nameof(countQueryOption));
            }

            if (validationSettings == null)
            {
                throw Error.ArgumentNull(nameof(validationSettings));
            }

            ODataPath path = countQueryOption.Context.Path;

            if (path != null && path.Count > 0)
            {
                IEdmProperty property = countQueryOption.Context.TargetProperty;
                IEdmStructuredType structuredType = countQueryOption.Context.TargetStructuredType;
                string name = countQueryOption.Context.TargetName;
                if (EdmHelpers.IsNotCountable(property, structuredType,
                    countQueryOption.Context.Model,
                    countQueryOption.Context.DefaultQueryConfigurations.EnableCount))
                {
                    if (property == null)
                    {
                        throw new InvalidOperationException(Error.Format(SRResources.NotCountableEntitySetUsedForCount, name));
                    }
                    else
                    {
                        throw new InvalidOperationException(Error.Format(SRResources.NotCountablePropertyUsedForCount, name));
                    }
                }
            }
        }
    }
}
