using System;
using MozaPlugin.Sdk.Coap;

namespace MozaPlugin.Sdk
{
    /// <summary>
    /// Abstract base for every URI that the SDK CoAP server can respond to.
    /// Phase 6 splits the wire layer (<see cref="CoapMessage"/>) from the
    /// resource layer: the UDP listener (Stream 7) decodes a datagram, resolves
    /// it against <see cref="CoapResourceRegistry"/>, then dispatches to a
    /// handler subclass via <see cref="HandleGet"/> or <see cref="HandlePost"/>.
    /// Subclasses do not see CoAP framing — they receive a
    /// <see cref="CoapResourceRequest"/> with the parsed Uri-Path, payload,
    /// and content-format option, and return a <see cref="CoapResourceResponse"/>
    /// describing the response code + body the listener should put on the wire.
    /// </summary>
    /// <remarks>
    /// Returning <see cref="CoapResourceResponse.MethodNotAllowed"/> from a GET
    /// or POST overload is the canonical way for a resource to advertise that
    /// the verb is unsupported (PitHouse parity: write-only resources reply
    /// 4.05 to GET; read-only resources reply 4.05 to POST).
    /// </remarks>
    public abstract class CoapResourceHandler
    {
        /// <summary>
        /// Handle a GET. Override to return a body + content-format. The
        /// default implementation returns 4.05 Method Not Allowed so read-only
        /// resources can simply skip overriding this.
        /// </summary>
        public virtual CoapResourceResponse HandleGet(CoapResourceRequest req)
            => CoapResourceResponse.MethodNotAllowed();

        /// <summary>
        /// Handle a POST. Override to consume the request body and apply a
        /// side effect; typically returns 2.03 Valid with an empty payload.
        /// Default is 4.05 Method Not Allowed.
        /// </summary>
        public virtual CoapResourceResponse HandlePost(CoapResourceRequest req)
            => CoapResourceResponse.MethodNotAllowed();

        /// <summary>
        /// True if this resource supports CoAP Observe (RFC 7641). The wire
        /// listener inspects this flag when an incoming GET carries an Observe
        /// option — handlers that return false simply ignore Observe and
        /// respond with a one-shot 2.05.
        /// </summary>
        public virtual bool SupportsObserve => false;
    }

    /// <summary>
    /// Immutable request descriptor handed to <see cref="CoapResourceHandler"/>.
    /// Carries the URI segments already resolved by the registry plus the raw
    /// request body bytes. Handlers must not retain the buffers past the call
    /// — the listener owns the lifetime of <see cref="Payload"/> and
    /// <see cref="Token"/>.
    /// </summary>
    public readonly struct CoapResourceRequest
    {
        /// <summary>
        /// Full canonical URI path of the request, e.g.
        /// <c>/MOZARacing/ProductDevice/&lt;id&gt;/FfbStrength</c>. Always begins
        /// with a leading slash. Useful for logging; handlers should normally
        /// use <see cref="DeviceId"/> and <see cref="PropertyName"/> instead.
        /// </summary>
        public string UriPath { get; }

        /// <summary>The device-ID segment extracted by the registry, or null when the URI has no <c>{id}</c> placeholder.</summary>
        public string? DeviceId { get; }

        /// <summary>The trailing property-name segment, or null when the URI has no property placeholder.</summary>
        public string? PropertyName { get; }

        /// <summary>Request body bytes. Empty for GET. Caller-owned; handler must not retain past the call.</summary>
        public byte[] Payload { get; }

        /// <summary>
        /// Content-Format option value, or -1 when the option is absent.
        /// 42 = application/octet-stream, 60 = application/cbor (see
        /// <see cref="CoapContentFormat"/>).
        /// </summary>
        public int ContentFormat { get; }

        /// <summary>The exchange token. Echoed verbatim by the listener on the response.</summary>
        public byte[] Token { get; }

        public CoapResourceRequest(
            string uriPath,
            string? deviceId,
            string? propertyName,
            byte[]? payload,
            int contentFormat,
            byte[]? token)
        {
            UriPath = uriPath ?? string.Empty;
            DeviceId = deviceId;
            PropertyName = propertyName;
            Payload = payload ?? Array.Empty<byte>();
            ContentFormat = contentFormat;
            Token = token ?? Array.Empty<byte>();
        }

        /// <summary>True when the request carries a non-empty body.</summary>
        public bool HasPayload => Payload != null && Payload.Length > 0;
    }

    /// <summary>
    /// Immutable response descriptor returned by <see cref="CoapResourceHandler"/>.
    /// The listener turns this into a CoAP message: <see cref="ResponseCode"/>
    /// goes into the message Code byte, <see cref="Payload"/> becomes the
    /// payload (with a 0xFF marker), and <see cref="ContentFormat"/> — when
    /// not -1 — is emitted as a Content-Format option.
    /// </summary>
    public readonly struct CoapResourceResponse
    {
        /// <summary>CoAP response code byte (see <see cref="CoapCode"/> for the constants).</summary>
        public byte ResponseCode { get; }

        /// <summary>Response body bytes. Empty when there is no body.</summary>
        public byte[] Payload { get; }

        /// <summary>
        /// Content-Format to emit alongside the response, or -1 to omit the
        /// option entirely. Use 42 for ASCII octet-stream scalars, 60 for CBOR.
        /// </summary>
        public int ContentFormat { get; }

        /// <summary>Optional human-readable reason (logged, not put on the wire).</summary>
        public string? Reason { get; }

        public CoapResourceResponse(byte responseCode, byte[]? payload, int contentFormat, string? reason = null)
        {
            ResponseCode = responseCode;
            Payload = payload ?? Array.Empty<byte>();
            ContentFormat = contentFormat;
            Reason = reason;
        }

        /// <summary>2.03 Valid — empty body, no Content-Format. The canonical POST acknowledgement shape.</summary>
        public static CoapResourceResponse Valid()
            => new CoapResourceResponse(CoapCode.Valid, Array.Empty<byte>(), -1);

        /// <summary>2.05 Content with the supplied payload bytes and Content-Format.</summary>
        public static CoapResourceResponse Content(byte[] payload, int contentFormat)
            => new CoapResourceResponse(CoapCode.Content, payload, contentFormat);

        /// <summary>4.04 Not Found.</summary>
        public static CoapResourceResponse NotFound(string? reason = null)
            => new CoapResourceResponse(CoapCode.NotFound, Array.Empty<byte>(), -1, reason);

        /// <summary>4.05 Method Not Allowed. Used by GET-only resources to reject POST, and vice versa.</summary>
        public static CoapResourceResponse MethodNotAllowed(string? reason = null)
            => new CoapResourceResponse(CoapCode.MethodNotAllowed, Array.Empty<byte>(), -1, reason);

        /// <summary>4.00 Bad Request — malformed payload (bad length, bad CBOR, etc.).</summary>
        public static CoapResourceResponse BadRequest(string? reason = null)
            => new CoapResourceResponse(CoapCode.BadRequest, Array.Empty<byte>(), -1, reason);

        /// <summary>5.00 Internal Server Error — exception inside a handler.</summary>
        public static CoapResourceResponse InternalError(string? reason = null)
            => new CoapResourceResponse(CoapCode.InternalServerError, Array.Empty<byte>(), -1, reason);
    }
}
