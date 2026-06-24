namespace OktaUserManager.Models;

/// <summary>One target field in the Okta user profile.</summary>
public record OktaProfileField(string Name, bool Required = false);

/// <summary>
/// The standard Okta "default" user profile attributes. These are the values a
/// CSV column can be mapped onto. Edit this list to add custom profile
/// attributes your Okta org defines.
/// </summary>
public static class OktaProfileFields
{
    public static readonly IReadOnlyList<OktaProfileField> All = new List<OktaProfileField>
    {
        // The four attributes Okta requires to create a user in the default profile.
        new("login",  Required: true),
        new("email",  Required: true),
        new("firstName", Required: true),
        new("lastName",  Required: true),

        // Common optional attributes.
        new("secondEmail"),
        new("middleName"),
        new("honorificPrefix"),
        new("honorificSuffix"),
        new("title"),
        new("displayName"),
        new("nickName"),
        new("profileUrl"),
        new("primaryPhone"),
        new("mobilePhone"),
        new("streetAddress"),
        new("city"),
        new("state"),
        new("zipCode"),
        new("countryCode"),
        new("postalAddress"),
        new("preferredLanguage"),
        new("locale"),
        new("timezone"),
        new("userType"),
        new("employeeNumber"),
        new("costCenter"),
        new("organization"),
        new("division"),
        new("department"),
        new("managerId"),
        new("manager"),
    };

    public static IEnumerable<string> RequiredNames =>
        All.Where(f => f.Required).Select(f => f.Name);
}
