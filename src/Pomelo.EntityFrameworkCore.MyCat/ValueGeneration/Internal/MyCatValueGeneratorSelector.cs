﻿using System;
using JetBrains.Annotations;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Utilities;

namespace Microsoft.EntityFrameworkCore.ValueGeneration.Internal
{
    public class MyCatValueGeneratorSelector : RelationalValueGeneratorSelector
    {
        public MyCatValueGeneratorSelector(
            [NotNull] IValueGeneratorCache cache,
            [NotNull] IRelationalAnnotationProvider relationalExtensions)
            : base(cache, relationalExtensions)
        {
        }

        public override ValueGenerator Create(IProperty property, IEntityType entityType)
        {
            Check.NotNull(property, nameof(property));
            Check.NotNull(entityType, nameof(entityType));

            var ret = property.ClrType.UnwrapNullableType() == typeof(Guid)
                ? property.ValueGenerated == ValueGenerated.Never
                  || property.MyCat().DefaultValueSql != null
                    ? (ValueGenerator)new TemporaryGuidValueGenerator()
                    : new SequentialGuidValueGenerator()
                : base.Create(property, entityType);
            return ret;
        }
    }
}