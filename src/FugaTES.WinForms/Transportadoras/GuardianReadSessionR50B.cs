using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using FugaTES.Aplicacao.Contratos.Recebimento;
using FugaTES.Aplicacao.Transportadoras;
using FugaTES.HostIntegracao;
using FugaTES.Integracao.Guardian;

namespace FugaTES.WinForms.Transportadoras;

public sealed record GuardianReadObservationR50B(string State, int? AuthenticationHttp, int? GetHttp,
    bool CodeMatch = false, bool ScopeConfirmed = false);

// Session-only R43 composition: existing secure credentials/token/GET client; no business writer.
public sealed class GuardianReadSessionR50B : IDisposable
{
    private readonly HttpClient http;
    private readonly GuardianTokenClient tokens;
    private readonly ScopedTransport transport;
    private readonly DateTimeOffset until;
    private int attempted;
    private readonly bool authApproved;
    public bool Available => authApproved && Volatile.Read(ref attempted) == 0 && DateTimeOffset.UtcNow < until;

    public GuardianReadSessionR50B(ICredenciaisGuardianSet26Provider credentials, HttpMessageHandler handler,
        bool authenticationAuthorized, DateTimeOffset untilUtc)
    {
        until = untilUtc;authApproved=authenticationAuthorized;
        transport = new ScopedTransport(handler, authenticationAuthorized, untilUtc);
        http = new HttpClient(transport) { Timeout = TimeSpan.FromSeconds(30), MaxResponseContentBufferSize = 1024 * 1024 };
        tokens = new GuardianTokenClient(http, new GuardianHttpOptions
        {
            BaseUrl = new Uri("https://guardian.cloudprix.com.br"), Timeout = TimeSpan.FromSeconds(30),
            TokenCacheLifetime = TimeSpan.Zero // no retained token after the one authorized GET
        }, credentials);
    }

    public static GuardianReadSessionR50B? Create(string directory)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "Guardian.read.R50B.json")));
            var p = doc.RootElement;
            if (p.GetProperty("Environment").GetString() != "HML" || p.GetProperty("Code").GetString() != "20000014" ||
                p.GetProperty("ApprovalReference").GetString() != "R50B_USER_AUTH_CREATE_TOKEN_AND_SINGLE_GET" ||
                !p.GetProperty("AuthenticationOnlyApproved").GetBoolean()) return null;
            var path = p.GetProperty("ExistingProfilePath").GetString()!;
            if (Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))) != p.GetProperty("ExistingProfileSHA256").GetString()) return null;
            var config = CarrierDesktopProfile.Load(path); // reads reference metadata only, no runtime/database startup
            if (config["Carrier:Environment"] != "HML" || config["Guardian:BaseUrl"] != "https://guardian.cloudprix.com.br") return null;
            var expiry = p.GetProperty("UntilUtc").GetDateTimeOffset();
            if (DateTimeOffset.UtcNow >= expiry) return null;
            return new(new CarrierServiceGuardianCredentials(config), new HttpClientHandler { AllowAutoRedirect = false }, true, expiry);
        }
        catch { return null; }
    }

    public async Task<GuardianReadObservationR50B> ReadAsync(string code, string approvedGetScope, CancellationToken ct, bool scopeAttested=false)
    {
        if (!scopeAttested || code != "20000014" || string.IsNullOrWhiteSpace(approvedGetScope) || approvedGetScope.Length > 64 ||
            !approvedGetScope.All(c => c is >= '0' and <= '9')) return new("BLOCKED_SCOPE", null, null);
        if (!Available || Interlocked.CompareExchange(ref attempted, 1, 0) != 0) return new("BLOCKED_NO_RETRY", null, null);
        transport.GetScope = approvedGetScope;
        try
        {
            var contract = new GuardianCarrierManualContract(true, true, "R43_ATTESTED_SCOPE_R50B_MANUAL_SESSION",
                "/api/v0/Guardian/Transportadora/{bp}?idAssinatura=" + Uri.EscapeDataString(approvedGetScope), "codigo", "razaoSocial", true);
            var adapter = new GuardianCarrierManualReadOnlyAdapter(http, new GuardianHttpOptions
            { BaseUrl = new Uri("https://guardian.cloudprix.com.br"), Timeout = TimeSpan.FromSeconds(30) }, tokens, contract);
            var result = await adapter.ConsultarCodigoAsync(new(code, approvedGetScope, true, contract.EvidenceReference), ct).ConfigureAwait(false);
            if (transport.GetStatus is 401 or 403) return Observation("INDETERMINATE_AUTH");
            if (transport.GetStatus == 404 && result.State == CarrierLookupState.Absent && result.Ausencia404Comprovada)
                return Observation("SCOPED_NOT_FOUND", scope: true);
            if (transport.GetStatus == 200 && result.State == CarrierLookupState.Unique && result.Codigo == code)
                return Observation("EXISTS", match: true, scope: true);
            return Observation("INDETERMINATE_RESPONSE");
        }
        catch (OperationCanceledException) { return Observation(ct.IsCancellationRequested ? "CANCELLED" : "INDETERMINATE_TIMEOUT"); }
        catch (GuardianHttpException ex) when (ex.InnerException is OperationCanceledException) { return Observation(ct.IsCancellationRequested ? "CANCELLED" : "INDETERMINATE_TIMEOUT"); }
        catch
        {
            return Observation(transport.AuthStatus is 401 or 403 ? "INDETERMINATE_AUTH" :
                transport.AuthCount == 0 ? "BLOCKED_CREDENTIALS_OR_AUTHORIZATION" : "INDETERMINATE_RESPONSE");
        }
        finally { transport.GetScope = null; } // never retain scope, payload or token as session evidence
    }

    private GuardianReadObservationR50B Observation(string state, bool match = false, bool scope = false)
        => new(state, transport.AuthStatus, transport.GetStatus, match, scope);
    public void Dispose() => http.Dispose();

    private sealed class ScopedTransport(HttpMessageHandler inner, bool authApproved, DateTimeOffset expiry) : DelegatingHandler(inner)
    {
        public string? GetScope;
        public int AuthCount, GetCount;
        public int? AuthStatus, GetStatus;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var u = request.RequestUri;
            if (!authApproved || DateTimeOffset.UtcNow >= expiry || u is null || u.UserInfo.Length != 0 || u.Fragment.Length != 0 ||
                u.GetLeftPart(UriPartial.Authority) != "https://guardian.cloudprix.com.br") throw new InvalidOperationException("R50B_ORIGIN_OR_AUTHORIZATION_BLOCKED");
            bool auth = request.Method == HttpMethod.Post && u.AbsolutePath == "/api/v2/CreateToken" && u.Query.Length == 0;
            bool get = request.Method == HttpMethod.Get && u.AbsolutePath == "/api/v0/Guardian/Transportadora/20000014" &&
                GetScope is not null && u.Query == "?idAssinatura=" + Uri.EscapeDataString(GetScope) && AuthStatus == 200;
            if (auth ? Interlocked.Increment(ref AuthCount) != 1 : !get || Interlocked.Increment(ref GetCount) != 1)
                throw new InvalidOperationException("R50B_BUSINESS_WRITE_OR_RETRY_BLOCKED");
            var response = await base.SendAsync(request, ct).ConfigureAwait(false);
            if (auth) AuthStatus = (int)response.StatusCode; else GetStatus = (int)response.StatusCode;
            return response;
        }
    }
}
