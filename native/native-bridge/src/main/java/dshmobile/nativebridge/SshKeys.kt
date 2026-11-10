package dshmobile.nativebridge

import android.security.keystore.KeyGenParameterSpec
import android.security.keystore.KeyInfo
import android.security.keystore.KeyProperties
import com.jcraft.jsch.Identity
import java.io.ByteArrayOutputStream
import java.math.BigInteger
import java.security.KeyFactory
import java.security.KeyPairGenerator
import java.security.KeyStore
import java.security.MessageDigest
import java.security.PrivateKey
import java.security.Signature
import java.security.interfaces.ECPublicKey
import java.security.spec.ECGenParameterSpec
import java.util.Base64

/**
 * SSH wire encodings for `ecdsa-sha2-nistp256` (RFC 5656 / RFC 4253). Pure functions: unit-tested on the JVM
 * against the JDK's own ECDSA (TASK-012, ADR-0006).
 */
internal object SshWire {
    const val ALG = "ecdsa-sha2-nistp256"
    private const val CURVE = "nistp256"

    /** Public key blob: string(alg) || string("nistp256") || string(0x04 || X || Y). */
    fun ecdsaPublicBlob(x: BigInteger, y: BigInteger): ByteArray {
        val point = byteArrayOf(0x04) + fixed32(x) + fixed32(y)
        return ByteArrayOutputStream().apply {
            writeString(ALG.toByteArray())
            writeString(CURVE.toByteArray())
            writeString(point)
        }.toByteArray()
    }

    /**
     * Converts a DER ECDSA signature (SEQUENCE { INTEGER r, INTEGER s }, what Android Keystore returns) to the SSH
     * signature blob: string(alg) || string(mpint(r) || mpint(s)).
     */
    fun derToSshSignature(der: ByteArray): ByteArray {
        val (r, s) = parseDerSignature(der)
        val inner = ByteArrayOutputStream().apply { writeMpint(r); writeMpint(s) }.toByteArray()
        return ByteArrayOutputStream().apply {
            writeString(ALG.toByteArray())
            writeString(inner)
        }.toByteArray()
    }

    /** `ecdsa-sha2-nistp256 AAAA… comment`, the line users put in `authorized_keys`. */
    fun openSshLine(blob: ByteArray, comment: String): String =
        "$ALG ${Base64.getEncoder().encodeToString(blob)} $comment"

    /** OpenSSH-style fingerprint of a key blob: `SHA256:` + unpadded base64 of SHA-256(blob). */
    fun fingerprint(blob: ByteArray): String =
        "SHA256:" + Base64.getEncoder().withoutPadding().encodeToString(MessageDigest.getInstance("SHA-256").digest(blob))

    internal fun parseDerSignature(der: ByteArray): Pair<BigInteger, BigInteger> {
        var i = 0
        require(der[i++].toInt() == 0x30) { "not a DER sequence" }
        i += derLengthSize(der, i)
        fun integer(): BigInteger {
            require(der[i++].toInt() == 0x02) { "not a DER integer" }
            val len = derLength(der, i)
            i += derLengthSize(der, i)
            val v = BigInteger(1, der.copyOfRange(i, i + len))
            i += len
            return v
        }
        return integer() to integer()
    }

    private fun derLength(b: ByteArray, at: Int): Int {
        val first = b[at].toInt() and 0xff
        if (first < 0x80) return first
        var len = 0
        for (k in 1..(first and 0x7f)) len = (len shl 8) or (b[at + k].toInt() and 0xff)
        return len
    }

    private fun derLengthSize(b: ByteArray, at: Int): Int {
        val first = b[at].toInt() and 0xff
        return if (first < 0x80) 1 else 1 + (first and 0x7f)
    }

    private fun fixed32(v: BigInteger): ByteArray {
        val raw = v.toByteArray().let { if (it.size > 32 && it[0].toInt() == 0) it.copyOfRange(1, it.size) else it }
        require(raw.size <= 32) { "coordinate too large" }
        return ByteArray(32 - raw.size) + raw
    }

    private fun ByteArrayOutputStream.writeUint32(v: Int) {
        write(v ushr 24 and 0xff); write(v ushr 16 and 0xff); write(v ushr 8 and 0xff); write(v and 0xff)
    }

    private fun ByteArrayOutputStream.writeString(b: ByteArray) {
        writeUint32(b.size)
        write(b)
    }

    /** SSH mpint: two's complement, minimal, with a leading 0 when the high bit is set. */
    private fun ByteArrayOutputStream.writeMpint(v: BigInteger) = writeString(v.toByteArray())
}

/**
 * The phone's SSH identity: a non-exportable EC P-256 signing key in Android Keystore. JSch signs through
 * [KeystoreIdentity]; the private key never leaves the Keystore (ADR-0006).
 */
internal object SshKeys {
    private const val ALIAS = "dshmobile-ssh-ecdsa-p256"
    private const val COMMENT = "dsh-mobile"

    private fun keyStore(): KeyStore = KeyStore.getInstance("AndroidKeyStore").apply { load(null) }

    fun exists(): Boolean = keyStore().containsAlias(ALIAS)

    /** Creates the key on first use. Idempotent. */
    @Synchronized
    fun ensure() {
        if (exists()) return
        val gen = KeyPairGenerator.getInstance(KeyProperties.KEY_ALGORITHM_EC, "AndroidKeyStore")
        gen.initialize(
            KeyGenParameterSpec.Builder(ALIAS, KeyProperties.PURPOSE_SIGN)
                .setAlgorithmParameterSpec(ECGenParameterSpec("secp256r1"))
                .setDigests(KeyProperties.DIGEST_SHA256)
                .build(),
        )
        gen.generateKeyPair()
    }

    private fun publicKey(): ECPublicKey = keyStore().getCertificate(ALIAS).publicKey as ECPublicKey

    private fun privateKey(): PrivateKey = keyStore().getKey(ALIAS, null) as PrivateKey

    fun publicBlob(): ByteArray = publicKey().w.let { SshWire.ecdsaPublicBlob(it.affineX, it.affineY) }

    fun openSshLine(): String = SshWire.openSshLine(publicBlob(), COMMENT)

    fun fingerprint(): String = SshWire.fingerprint(publicBlob())

    /** Whether Keystore reports the key as hardware-backed (TEE/StrongBox). Reported as is; never assumed. */
    fun hardwareBacked(): Boolean? = runCatching {
        val key = privateKey()
        val info = KeyFactory.getInstance(key.algorithm, "AndroidKeyStore").getKeySpec(key, KeyInfo::class.java)
        if (android.os.Build.VERSION.SDK_INT >= 31) {
            info.securityLevel != KeyProperties.SECURITY_LEVEL_SOFTWARE
        } else {
            @Suppress("DEPRECATION") info.isInsideSecureHardware
        }
    }.getOrNull()

    fun signSsh(data: ByteArray): ByteArray {
        val der = Signature.getInstance("SHA256withECDSA").run {
            initSign(privateKey())
            update(data)
            sign()
        }
        return SshWire.derToSshSignature(der)
    }

    /** JSch view of the Keystore key. */
    class KeystoreIdentity : Identity {
        override fun setPassphrase(passphrase: ByteArray?): Boolean = true
        override fun getPublicKeyBlob(): ByteArray = publicBlob()
        override fun getSignature(data: ByteArray): ByteArray = signSsh(data)
        override fun getSignature(data: ByteArray, alg: String?): ByteArray = signSsh(data)
        @Deprecated("JSch legacy API") override fun decrypt(): Boolean = true
        override fun getAlgName(): String = SshWire.ALG
        override fun getName(): String = "android-keystore:$ALIAS"
        override fun isEncrypted(): Boolean = false
        override fun clear() = Unit
    }
}
