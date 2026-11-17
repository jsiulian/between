import Foundation
import CryptoKit

let alicePrivRaw: [UInt8] = Array((0..<32).map { UInt8($0 & 0xFF) })
let bobPrivRaw: [UInt8] = [0xDE, 0xAD, 0xBE, 0xEF] + Array((0..<28).map { UInt8($0 & 0xFF) })
let alicePriv = try! P256.KeyAgreement.PrivateKey(rawRepresentation: Data(alicePrivRaw))
let bobPriv = try! P256.KeyAgreement.PrivateKey(rawRepresentation: Data(bobPrivRaw))

let bobPub64 = bobPriv.publicKey.rawRepresentation // 64 bytes per empirical result

// 1) Encrypt using the 64-byte X||Y form (what Encrypt.swift receives on the wire)
let sharedSecret = try! alicePriv.sharedSecretFromKeyAgreement(with: try! P256.KeyAgreement.PublicKey(rawRepresentation: bobPub64))
let symmetricKey = sharedSecret.hkdfDerivedSymmetricKey(
    using: SHA256.self,
    salt: Data(),
    sharedInfo: "P2P-Secure-Channel-Context".data(using: .utf8)!,
    outputByteCount: 32
)
let sealedBox = try! AES.GCM.seal("interop-test-message".data(using: .utf8)!, using: symmetricKey)
let combined = sealedBox.combined!
let keyData = symmetricKey.withUnsafeBytes { Data($0) }

// 2) Also test: does PublicKey(rawRepresentation:) accept the 65-byte 04||X||Y form?
let bobSec1 = Data([0x04]) + bobPub64
let acceptsSec1 = (try? P256.KeyAgreement.PublicKey(rawRepresentation: bobSec1)) != nil
print("accepts 65-byte 04||X||Y: \(acceptsSec1)")

try! alicePriv.publicKey.rawRepresentation.base64EncodedString().write(toFile: "vec_alice_pub.b64", atomically: true, encoding: .utf8)
try! bobPub64.base64EncodedString().write(toFile: "vec_bob_pub.b64", atomically: true, encoding: .utf8)
try! keyData.base64EncodedString().write(toFile: "vec_key.b64", atomically: true, encoding: .utf8)
try! combined.base64EncodedString().write(toFile: "vec_ct.b64", atomically: true, encoding: .utf8)
print("alice pub: \(alicePriv.publicKey.rawRepresentation.base64EncodedString())")
print("bob pub:   \(bobPub64.base64EncodedString())")
print("key:       \(keyData.base64EncodedString())")
print("ct:        \(combined.base64EncodedString())")
