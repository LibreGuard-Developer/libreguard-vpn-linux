using System.Text.Json.Serialization;

namespace Libreguard.Vpn.Linux.Models;

public sealed record AuthSession(
    string Token,
    string RefreshToken,
    string Email,
    string UserId,
    string DeviceId,
    int ActiveDevices,
    int MaxDevices,
    string PlanType);

public sealed record LoginRequest(
    [property: JsonPropertyName("email")] string Email,
    [property: JsonPropertyName("password")] string Password,
    [property: JsonPropertyName("deviceId")] string DeviceId,
    [property: JsonPropertyName("appVersion")] string AppVersion,
    [property: JsonPropertyName("devicePublicKey")] string DevicePublicKey,
    [property: JsonPropertyName("devicePublicKeyId")] string DevicePublicKeyId,
    [property: JsonPropertyName("devicePublicKeyAlgorithm")] string DevicePublicKeyAlgorithm);

public sealed class LoginResponse
{
    [JsonPropertyName("success")]
    public bool? SuccessRaw { get; init; }

    [JsonPropertyName("token")]
    public string? Token { get; init; }

    [JsonPropertyName("refreshToken")]
    public string? RefreshToken { get; init; }

    [JsonPropertyName("email")]
    public string? Email { get; init; }

    [JsonPropertyName("userId")]
    public string? UserId { get; init; }

    [JsonPropertyName("deviceId")]
    public string? DeviceId { get; init; }

    [JsonPropertyName("activeDevices")]
    public int ActiveDevices { get; init; }

    [JsonPropertyName("maxDevices")]
    public int MaxDevices { get; init; }

    [JsonPropertyName("planType")]
    public string? PlanType { get; init; }

    [JsonPropertyName("requiresTwoFactor")]
    public bool RequiresTwoFactor { get; init; }

    [JsonPropertyName("pendingLoginToken")]
    public string? PendingLoginToken { get; init; }

    [JsonPropertyName("message")]
    public string? Message { get; init; }

    [JsonIgnore]
    public bool Success => SuccessRaw ?? (RequiresTwoFactor || (!string.IsNullOrWhiteSpace(Token) && !string.IsNullOrWhiteSpace(RefreshToken)));
}

public sealed record RegisterRequest(
    [property: JsonPropertyName("email")] string Email,
    [property: JsonPropertyName("password")] string Password,
    [property: JsonPropertyName("confirmPassword")] string ConfirmPassword,
    [property: JsonPropertyName("appVersion")] string? AppVersion = null);

public sealed record RegisterResponse(
    [property: JsonPropertyName("success")] bool Success,
    [property: JsonPropertyName("userId")] string? UserId,
    [property: JsonPropertyName("email")] string? Email,
    [property: JsonPropertyName("requiresEmailConfirmation")] bool RequiresEmailConfirmation,
    [property: JsonPropertyName("message")] string? Message);

public sealed record EmailConfirmationStatus(
    [property: JsonPropertyName("emailConfirmed")] bool EmailConfirmed,
    [property: JsonPropertyName("email")] string? Email,
    [property: JsonPropertyName("userId")] string? UserId,
    [property: JsonPropertyName("message")] string? Message);

public sealed record TwoFactorVerifyRequest(
    [property: JsonPropertyName("email")] string Email,
    [property: JsonPropertyName("twoFactorCode")] string TwoFactorCode,
    [property: JsonPropertyName("pendingLoginToken")] string PendingLoginToken,
    [property: JsonPropertyName("deviceId")] string DeviceId,
    [property: JsonPropertyName("appVersion")] string AppVersion,
    [property: JsonPropertyName("devicePublicKey")] string DevicePublicKey,
    [property: JsonPropertyName("devicePublicKeyId")] string DevicePublicKeyId,
    [property: JsonPropertyName("devicePublicKeyAlgorithm")] string DevicePublicKeyAlgorithm);

public sealed record RecoveryCodeVerifyRequest(
    [property: JsonPropertyName("email")] string Email,
    [property: JsonPropertyName("recoveryCode")] string RecoveryCode,
    [property: JsonPropertyName("pendingLoginToken")] string PendingLoginToken,
    [property: JsonPropertyName("deviceId")] string DeviceId,
    [property: JsonPropertyName("appVersion")] string AppVersion,
    [property: JsonPropertyName("devicePublicKey")] string DevicePublicKey,
    [property: JsonPropertyName("devicePublicKeyId")] string DevicePublicKeyId,
    [property: JsonPropertyName("devicePublicKeyAlgorithm")] string DevicePublicKeyAlgorithm);

public sealed record RefreshTokenRequest(
    [property: JsonPropertyName("refreshToken")] string RefreshToken,
    [property: JsonPropertyName("deviceId")] string DeviceId,
    [property: JsonPropertyName("appVersion")] string AppVersion,
    [property: JsonPropertyName("devicePublicKey")] string DevicePublicKey,
    [property: JsonPropertyName("devicePublicKeyId")] string DevicePublicKeyId,
    [property: JsonPropertyName("devicePublicKeyAlgorithm")] string DevicePublicKeyAlgorithm);

public sealed record ForgotPasswordRequest([property: JsonPropertyName("email")] string Email);

public sealed record ResetPasswordRequest(
    [property: JsonPropertyName("email")] string Email,
    [property: JsonPropertyName("token")] string Token,
    [property: JsonPropertyName("newPassword")] string NewPassword);

public sealed record GoogleLoginRequest(
    [property: JsonPropertyName("idToken")] string IdToken,
    [property: JsonPropertyName("deviceId")] string DeviceId,
    [property: JsonPropertyName("appVersion")] string AppVersion,
    [property: JsonPropertyName("devicePublicKey")] string DevicePublicKey,
    [property: JsonPropertyName("devicePublicKeyId")] string DevicePublicKeyId,
    [property: JsonPropertyName("devicePublicKeyAlgorithm")] string DevicePublicKeyAlgorithm);

public sealed record GoogleCodeLoginRequest(
    [property: JsonPropertyName("clientId")] string ClientId,
    [property: JsonPropertyName("code")] string Code,
    [property: JsonPropertyName("redirectUri")] string RedirectUri,
    [property: JsonPropertyName("codeVerifier")] string CodeVerifier,
    [property: JsonPropertyName("deviceId")] string DeviceId,
    [property: JsonPropertyName("appVersion")] string AppVersion,
    [property: JsonPropertyName("devicePublicKey")] string DevicePublicKey,
    [property: JsonPropertyName("devicePublicKeyId")] string DevicePublicKeyId,
    [property: JsonPropertyName("devicePublicKeyAlgorithm")] string DevicePublicKeyAlgorithm);

public sealed record PreAuthOAuthDeviceRemovalRequest(
    [property: JsonPropertyName("provider")] string Provider,
    [property: JsonPropertyName("idToken")] string IdToken,
    [property: JsonPropertyName("deviceIdToRemove")] int DeviceIdToRemove);

public sealed record PreAuthDeviceRemovalRequest(
    [property: JsonPropertyName("email")] string Email,
    [property: JsonPropertyName("password")] string Password,
    [property: JsonPropertyName("deviceIdToRemove")] int DeviceIdToRemove);

public sealed record PreAuthOAuthCodeDeviceRemovalRequest(
    [property: JsonPropertyName("provider")] string Provider,
    [property: JsonPropertyName("clientId")] string ClientId,
    [property: JsonPropertyName("code")] string Code,
    [property: JsonPropertyName("redirectUri")] string RedirectUri,
    [property: JsonPropertyName("codeVerifier")] string CodeVerifier,
    [property: JsonPropertyName("deviceIdToRemove")] int DeviceIdToRemove);

public sealed class DeviceLimitExceededResponse
{
    [JsonPropertyName("message")]
    public string? Message { get; init; }

    [JsonPropertyName("errorCode")]
    public string? ErrorCode { get; init; }

    [JsonPropertyName("currentDevices")]
    public int CurrentDevices { get; init; }

    [JsonPropertyName("maxDevices")]
    public int MaxDevices { get; init; }

    [JsonPropertyName("planType")]
    public string? PlanType { get; init; }

    [JsonPropertyName("devices")]
    public IReadOnlyList<UserDevice>? Devices { get; init; }
}

public sealed record OAuthTokenRequest(
    [property: JsonPropertyName("email")] string Email,
    [property: JsonPropertyName("deviceId")] string DeviceId,
    [property: JsonPropertyName("appVersion")] string AppVersion,
    [property: JsonPropertyName("devicePublicKey")] string DevicePublicKey,
    [property: JsonPropertyName("devicePublicKeyId")] string DevicePublicKeyId,
    [property: JsonPropertyName("devicePublicKeyAlgorithm")] string DevicePublicKeyAlgorithm);

public sealed record OAuthCompleteRequest(
    [property: JsonPropertyName("email")] string Email,
    [property: JsonPropertyName("provider")] string Provider,
    [property: JsonPropertyName("deviceId")] string DeviceId,
    [property: JsonPropertyName("appVersion")] string AppVersion,
    [property: JsonPropertyName("devicePublicKey")] string DevicePublicKey,
    [property: JsonPropertyName("devicePublicKeyId")] string DevicePublicKeyId,
    [property: JsonPropertyName("devicePublicKeyAlgorithm")] string DevicePublicKeyAlgorithm);

public sealed class TokenCheckResponse
{
    [JsonPropertyName("isValid")]
    public bool? IsValid { get; init; }

    [JsonPropertyName("valid")]
    public bool? ValidRaw { get; init; }

    [JsonPropertyName("email")]
    public string? Email { get; init; }

    [JsonPropertyName("expiresAt")]
    public DateTimeOffset? ExpiresAt { get; init; }

    [JsonPropertyName("message")]
    public string? Message { get; init; }

    [JsonIgnore]
    public bool Valid => IsValid ?? ValidRaw ?? false;
}

public sealed class ApiMessage
{
    [JsonPropertyName("success")]
    public bool? SuccessRaw { get; init; }

    [JsonPropertyName("message")]
    public string? Message { get; init; }

    [JsonIgnore]
    public bool Success => SuccessRaw ?? true;
}

public sealed record TwoFactorStatus(
    [property: JsonPropertyName("is2faEnabled")] bool Is2faEnabled,
    [property: JsonPropertyName("hasAuthenticator")] bool HasAuthenticator,
    [property: JsonPropertyName("recoveryCodesLeft")] int RecoveryCodesLeft,
    [property: JsonPropertyName("message")] string? Message);

public sealed record TwoFactorSetup(
    [property: JsonPropertyName("sharedKey")] string? SharedKey,
    [property: JsonPropertyName("authenticatorUri")] string? AuthenticatorUri,
    [property: JsonPropertyName("manualEntryKey")] string? ManualEntryKey,
    [property: JsonPropertyName("message")] string? Message);

public sealed record TwoFactorCodeRequest([property: JsonPropertyName("code")] string Code);

public sealed record RecoveryCodesResponse(
    [property: JsonPropertyName("success")] bool Success,
    [property: JsonPropertyName("recoveryCodes")] IReadOnlyList<string>? RecoveryCodes,
    [property: JsonPropertyName("message")] string? Message);

public sealed record DeviceRegistrationPayload(
    string DeviceId,
    string AppVersion,
    string PublicKey,
    string PublicKeyId,
    string PublicKeyAlgorithm);

public sealed record EncryptedPassphrase(
    [property: JsonPropertyName("algorithm")] string Algorithm,
    [property: JsonPropertyName("keyId")] string KeyId,
    [property: JsonPropertyName("ciphertext")] string Ciphertext);
