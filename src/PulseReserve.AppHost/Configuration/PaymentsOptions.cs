using PulseReserve.Modules.Payments.Features;

namespace PulseReserve.AppHost.Configuration;

/// <summary>
/// Payments host configuration.
/// <list type="bullet">
/// <item><c>Payments:Simulator:Enabled</c> switches on the fake payment provider and its completion endpoint. Off by
/// default, and the policy fails CLOSED:
/// <list type="bullet">
/// <item><c>Development</c> - the setting alone is enough (the everyday developer workflow).</item>
/// <item><c>Testing</c> - the setting is NOT enough: <c>Payments:Simulator:AllowInTestingEnvironment=true</c> must also be set
/// explicitly. Test harnesses set it; a host that was deployed under that name by mistake does not have it and refuses to start.</item>
/// <item>Any other name - unset, misspelt, <c>Staging</c>, <c>Production</c>, anything - startup fails, with or without opt-ins.</item>
/// </list>
/// Environment names are compared case-insensitively but must otherwise match exactly. Both settings must be <c>true</c>/<c>false</c>
/// (a typo fails startup rather than being read as off or on), and enabling the simulator next to a real provider fails startup in
/// every environment. Without the simulator (and without a real provider adapter) payments are unavailable and payment
/// initiation answers 503; a real provider that is enabled but misconfigured is simply unavailable and NEVER replaced by the
/// simulator.</item>
/// <item><c>Notifications:DevAdapter:Enabled</c> enables the development notification adapter (it only logs ids, it
/// delivers nothing). Same environment restriction. Without an adapter, notification events are retried and then flagged
/// for review; purchases are never affected.</item>
/// <item><c>Payments:Workers:Enabled</c> (default true) runs the reconciliation, refund and outbox worker inside the
/// host; <c>Payments:Workers:IntervalSeconds</c> (default 15).</item>
/// <item><c>Payments:Workflow:*</c> tunes batches, lease, grace, staleness, backoff and attempt limits (all validated).</item>
/// </list>
/// </summary>
public sealed record PaymentsOptions(
    bool SimulatorEnabled, bool DevNotificationsEnabled, bool WorkersEnabled, TimeSpan WorkerInterval, WorkflowOptions Workflow)
{
    public const string SimulatorKey = "Payments:Simulator:Enabled";

    /// <summary>The explicit opt-in that the <c>Testing</c> environment needs before the simulator may start (see <see cref="Resolve"/>).</summary>
    public const string SimulatorTestingOptInKey = "Payments:Simulator:AllowInTestingEnvironment";
    public const string DevNotificationsKey = "Notifications:DevAdapter:Enabled";

    /// <summary>The settings that switch a real provider on (see <c>PaymentProvidersComposition</c>).</summary>
    public static readonly string[] RealProviderKeys = ["Payments:Stripe:Enabled", "Payments:Paymob:Enabled"];

    public static PaymentsOptions Resolve(IConfiguration configuration, IHostEnvironment environment)
    {
        var relaxed = environment.IsDevelopment() || environment.IsEnvironment("Testing");
        var simulator = ResolveSimulator(configuration, environment);
        var devNotifications = DevOnlyFlag(configuration, DevNotificationsKey, environment, relaxed, "development notification adapter");

        // Fail closed on an ambiguous provider selection: the simulator and a real provider are alternatives, never a mix.
        // A host that has a real provider enabled and also the simulator (a copied settings file, a stray variable) would offer
        // a payment method that confirms itself.
        if (simulator && RealProviderKeys.FirstOrDefault(key => Flag(configuration, key, defaultValue: false)) is { } enabledReal)
        {
            throw new InvalidOperationException(
                $"{SimulatorKey} is enabled together with {enabledReal}. Choose one: the simulator confirms payments by itself and must " +
                "never run next to a real provider. Remove the simulator setting.");
        }

        var workers = Flag(configuration, "Payments:Workers:Enabled", defaultValue: true);
        var interval = Seconds(configuration, "Payments:Workers:IntervalSeconds", 15);

        var defaults = new WorkflowOptions();
        var workflow = new WorkflowOptions
        {
            BatchSize = Count(configuration, "Payments:Workflow:BatchSize", defaults.BatchSize),
            Lease = Seconds(configuration, "Payments:Workflow:LeaseSeconds", defaults.Lease.TotalSeconds),
            CompletionGrace = Seconds(configuration, "Payments:Workflow:CompletionGraceSeconds", defaults.CompletionGrace.TotalSeconds, allowZero: true),
            StaleAttemptAfter = Seconds(configuration, "Payments:Workflow:StaleAttemptAfterSeconds", defaults.StaleAttemptAfter.TotalSeconds, allowZero: true),
            AbandonAttemptAfter = Seconds(configuration, "Payments:Workflow:AbandonAttemptAfterSeconds", defaults.AbandonAttemptAfter.TotalSeconds, allowZero: true),
            BackoffBase = Seconds(configuration, "Payments:Workflow:BackoffBaseSeconds", defaults.BackoffBase.TotalSeconds, allowZero: true),
            MaxBackoff = Seconds(configuration, "Payments:Workflow:MaxBackoffSeconds", defaults.MaxBackoff.TotalSeconds),
            MaxAttempts = Count(configuration, "Payments:Workflow:MaxAttempts", defaults.MaxAttempts)
        };

        return new PaymentsOptions(simulator, devNotifications, workers, interval, workflow);
    }

    /// <summary>
    /// The simulator confirms payments by itself, so it is decided in one place and fails closed: Development needs only the flag,
    /// Testing also needs the explicit opt-in, every other environment name is refused.
    /// </summary>
    private static bool ResolveSimulator(IConfiguration configuration, IHostEnvironment environment)
    {
        var enabled = Flag(configuration, SimulatorKey, defaultValue: false);
        var testingOptIn = Flag(configuration, SimulatorTestingOptInKey, defaultValue: false); // parsed always: a typo is never ignored
        if (!enabled)
            return false;

        if (environment.IsDevelopment())
            return true;

        if (environment.IsEnvironment("Testing"))
        {
            if (testingOptIn)
                return true;

            throw new InvalidOperationException(
                $"{SimulatorKey} is enabled in the 'Testing' environment without {SimulatorTestingOptInKey}=true. The payment simulator " +
                "confirms payments by itself; automated tests must opt in explicitly, and a deployed host must not run as 'Testing'. " +
                "Remove the simulator setting, or (for a test harness only) add the opt-in.");
        }

        throw new InvalidOperationException(
            $"{SimulatorKey} is enabled in the '{environment.EnvironmentName}' environment. The payment simulator is " +
            "development/test only and must never run in production; remove the setting.");
    }

    private static bool DevOnlyFlag(IConfiguration configuration, string key, IHostEnvironment environment, bool relaxed, string what)
    {
        var enabled = Flag(configuration, key, defaultValue: false);
        if (enabled && !relaxed)
        {
            throw new InvalidOperationException(
                $"{key} is enabled in the '{environment.EnvironmentName}' environment. The {what} is " +
                "development/test only and must never run in production; remove the setting.");
        }

        return enabled;
    }

    private static bool Flag(IConfiguration configuration, string key, bool defaultValue)
    {
        var raw = configuration[key];
        if (string.IsNullOrWhiteSpace(raw))
            return defaultValue;
        return bool.TryParse(raw, out var value) ? value : throw new InvalidOperationException($"{key} must be 'true' or 'false'.");
    }

    private static int Count(IConfiguration configuration, string key, int defaultValue)
    {
        var value = configuration.GetValue<int?>(key) ?? defaultValue;
        return value >= 1 ? value : throw new InvalidOperationException($"{key} must be at least 1.");
    }

    private static TimeSpan Seconds(IConfiguration configuration, string key, double defaultSeconds, bool allowZero = false)
    {
        var value = configuration.GetValue<double?>(key) ?? defaultSeconds;
        if (value < 0 || (!allowZero && value <= 0))
            throw new InvalidOperationException($"{key} must be {(allowZero ? "zero or more" : "greater than zero")} seconds.");
        return TimeSpan.FromSeconds(value);
    }
}
