# Signal Sealed Sender Facts

Spec: [fact-spec](../spec/fact-spec.md)

## 1. External Normative Sources

- **Signal Sealed Sender Protocol Documentation**: *Technology Preview: Sealed Sender*, Signal Messenger LLC, October 29, 2018.
- **Signal Unidentified Delivery Wire Schemas**: `UnidentifiedDelivery.proto` and `libsignal-service-java`, Signal Messenger LLC, 2018–2024.
- **Academic Traffic Analysis of Sealed Sender**: *Statistical Disclosure Attacks on Sealed Sender*, Proceedings of the Network and Distributed System Security Symposium (NDSS), 2021.

## 2. Axioms and Platform Invariants

1. **`FACT-SIGNAL-SEALED-SENDER-ACCESS-KEY` (Unidentified Access Key Derivation):** The Signal Sealed Sender protocol derives a 16-byte `UnidentifiedAccessKey` from the recipient's 32-byte secret profile key via HKDF-SHA256 (`info: "UnidentifiedAccessKey"`). The recipient registers this access key with untrusted delivery servers to authorize inbound message staging without requiring sender credentials.
2. **`FACT-SIGNAL-SEALED-SENDER-UNAUTH-CHANNEL` (Unauthenticated Transport Delivery):** In the Signal Sealed Sender protocol, clients transmit envelopes containing `(recipient_uuid, unidentified_access_key, sealed_envelope_ciphertext)` over unauthenticated network connections (omitting client HTTP Bearer tokens, account authentication cookies, and client TLS certificates), ensuring the intermediary routing server receives zero sender account credentials.
3. **`FACT-SIGNAL-SEALED-SENDER-INNER-AUTHENTICATION` (Inner-Payload Sender Authentication):** In the Signal Sealed Sender protocol, the plaintext sender identity and device authentication material reside exclusively inside the encrypted envelope package (decrypted solely by the recipient's private key), preventing untrusted transport intermediaries from reading sender identity headers on the wire.
