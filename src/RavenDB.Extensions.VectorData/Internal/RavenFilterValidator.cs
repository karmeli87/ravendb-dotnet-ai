using System.Linq.Expressions;
using Microsoft.Extensions.VectorData.ProviderServices;
using Microsoft.Extensions.VectorData.ProviderServices.Filter;

namespace RavenDB.Extensions.VectorData.Internal;

internal sealed class RavenFilterValidator : FilterTranslatorBase
{
    private RavenFilterValidator()
    {
    }

    public static void Validate(LambdaExpression filter, CollectionModel model)
    {
        var validator = new RavenFilterValidator();
        validator.PreprocessFilter(filter, model, new FilterPreprocessingOptions());
        new PropertyBinder(validator).Visit(filter.Body);
    }

    private sealed class PropertyBinder(RavenFilterValidator validator) : ExpressionVisitor
    {
        public override Expression? Visit(Expression? node)
        {
            if (node is not null && validator.TryBindProperty(node, out _))
            {
                return node;
            }

            return base.Visit(node);
        }
    }
}
