using Microsoft.AspNetCore.Mvc.ApplicationParts;
using Microsoft.AspNetCore.Mvc.Controllers;

namespace Ecommerce.Common.Controllers;

public static class ControllerRegistration
{
    public static IMvcBuilder AddCommerceControllers(
        this IServiceCollection services,
        IHostEnvironment environment)
    {
        return services.AddControllers(options =>
        {
            // Existing handlers validate nullable request members explicitly.
            options.SuppressImplicitRequiredAttributeForNonNullableReferenceTypes = true;
        }).ConfigureApplicationPartManager(manager =>
        {
            if (!environment.IsDevelopment())
                manager.FeatureProviders.Add(new ProductionControllerFeatureProvider());
        });
    }

    private sealed class ProductionControllerFeatureProvider : IApplicationFeatureProvider<ControllerFeature>
    {
        public void PopulateFeature(IEnumerable<ApplicationPart> parts, ControllerFeature feature)
        {
            foreach (var controller in feature.Controllers
                .Where(controller => controller.IsDefined(typeof(DevelopmentOnlyAttribute), inherit: false))
                .ToArray())
            {
                feature.Controllers.Remove(controller);
            }
        }
    }
}

[AttributeUsage(AttributeTargets.Class)]
internal sealed class DevelopmentOnlyAttribute : Attribute;
