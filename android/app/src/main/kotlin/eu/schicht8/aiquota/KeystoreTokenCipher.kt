package eu.schicht8.aiquota

import android.security.keystore.KeyGenParameterSpec
import android.security.keystore.KeyProperties
import eu.schicht8.aiquota.core.TokenCipher
import java.security.KeyStore
import javax.crypto.Cipher
import javax.crypto.KeyGenerator
import javax.crypto.SecretKey
import javax.crypto.spec.GCMParameterSpec

/** AES-GCM with a key that lives in the Android Keystore and never leaves it. The stored form
 * is the 12-byte IV followed by the ciphertext and tag. */
class KeystoreTokenCipher : TokenCipher {
    override fun encrypt(plain: ByteArray): ByteArray {
        val cipher = Cipher.getInstance(TRANSFORMATION).apply { init(Cipher.ENCRYPT_MODE, key()) }
        return cipher.iv + cipher.doFinal(plain)
    }

    override fun decrypt(encrypted: ByteArray): ByteArray {
        val iv = encrypted.copyOfRange(0, IV_LENGTH)
        val cipher = Cipher.getInstance(TRANSFORMATION).apply { init(Cipher.DECRYPT_MODE, key(), GCMParameterSpec(TAG_BITS, iv)) }
        return cipher.doFinal(encrypted, IV_LENGTH, encrypted.size - IV_LENGTH)
    }

    @Synchronized
    private fun key(): SecretKey {
        val store = KeyStore.getInstance(KEYSTORE).apply { load(null) }
        (store.getEntry(ALIAS, null) as? KeyStore.SecretKeyEntry)?.let { return it.secretKey }
        return KeyGenerator.getInstance(KeyProperties.KEY_ALGORITHM_AES, KEYSTORE).apply {
            init(
                KeyGenParameterSpec.Builder(ALIAS, KeyProperties.PURPOSE_ENCRYPT or KeyProperties.PURPOSE_DECRYPT)
                    .setBlockModes(KeyProperties.BLOCK_MODE_GCM)
                    .setEncryptionPaddings(KeyProperties.ENCRYPTION_PADDING_NONE)
                    .setKeySize(256)
                    .build(),
            )
        }.generateKey()
    }

    private companion object {
        const val KEYSTORE = "AndroidKeyStore"
        const val ALIAS = "aiquota-tokens"
        const val TRANSFORMATION = "AES/GCM/NoPadding"
        const val IV_LENGTH = 12
        const val TAG_BITS = 128
    }
}
