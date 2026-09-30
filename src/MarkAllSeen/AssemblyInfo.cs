using System.Security;
using System.Security.Permissions;

// The publicized game assemblies (RiskOfRain2.GameLibs) let the plugin use the game's private members; this makes the
// Mono runtime skip the access checks for them (see Assembly References on the R2Wiki).
#pragma warning disable CS0618 // SecurityAction.RequestMinimum is obsolete
[assembly: SecurityPermission(SecurityAction.RequestMinimum, SkipVerification = true)]
#pragma warning restore CS0618
