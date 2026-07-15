package com.vass.overlay

import android.content.ContentResolver
import android.content.Context
import android.content.Intent
import android.database.Cursor
import android.net.Uri
import android.os.Build
import android.provider.OpenableColumns
import android.util.Log
import java.io.File
import java.io.FileOutputStream
import java.util.UUID

internal object SharedContentStore {
  private const val TAG = "VassShare"
  private const val PREFS = "vass_shared_attachment"
  private const val KEY_REQUEST_ID = "requestId"
  private const val KEY_STATUS = "status"
  private const val KEY_KIND = "kind"
  private const val KEY_TEXT = "text"
  private const val KEY_URI = "uri"
  private const val KEY_MIME_TYPE = "mimeType"
  private const val KEY_ORIGINAL_NAME = "originalName"
  private const val KEY_ERROR = "error"
  private const val MAX_BYTES = 50L * 1024L * 1024L
  private const val MAX_TEXT_CHARS = 20_000

  fun capture(context: Context, intent: Intent) {
    val requestId = UUID.randomUUID().toString()
    try {
      val sharedText = extractText(intent)
      if (sharedText != null) {
        save(context, requestId, "ready", kind = "text", text = sharedText)
        return
      }
      val uri = extractUri(intent) ?: throw IllegalArgumentException("Shared intent does not contain a stream URI")
      val mimeType = normalizeMimeType(context.contentResolver.getType(uri) ?: intent.type)
      val originalName = displayName(context.contentResolver, uri)
      val copiedUri = copyToCache(context, uri, mimeType)
      save(context, requestId, "ready", kind = "attachment", uri = copiedUri, mimeType = mimeType, originalName = originalName)
    } catch (exception: Exception) {
      Log.e(TAG, "Could not stage shared attachment", exception)
      save(context, requestId, "error", error = "shared_attachment_unavailable")
    }
  }

  fun read(context: Context): Map<String, String?> {
    val prefs = context.getSharedPreferences(PREFS, Context.MODE_PRIVATE)
    return mapOf(
      "requestId" to prefs.getString(KEY_REQUEST_ID, null),
      "status" to prefs.getString(KEY_STATUS, null),
      "kind" to prefs.getString(KEY_KIND, null),
      "text" to prefs.getString(KEY_TEXT, null),
      "uri" to prefs.getString(KEY_URI, null),
      "mimeType" to prefs.getString(KEY_MIME_TYPE, null),
      "originalName" to prefs.getString(KEY_ORIGINAL_NAME, null),
      "error" to prefs.getString(KEY_ERROR, null),
    )
  }

  // The staged attachment keeps using this cache file until the assistant
  // consumes it, so acknowledgement clears the pending intent only.
  fun acknowledge(context: Context, requestId: String) {
    val prefs = context.getSharedPreferences(PREFS, Context.MODE_PRIVATE)
    if (prefs.getString(KEY_REQUEST_ID, null) == requestId) prefs.edit().clear().apply()
  }

  private fun save(
    context: Context,
    requestId: String,
    status: String,
    kind: String? = null,
    text: String? = null,
    uri: String? = null,
    mimeType: String? = null,
    originalName: String? = null,
    error: String? = null,
  ) {
    // The receiver starts React Native immediately after this call. apply() is
    // asynchronous, so a cold-started JS runtime could observe an empty store
    // before Android has persisted the attachment metadata. commit() makes the
    // hand-off durable before the launcher activity is brought forward.
    context.getSharedPreferences(PREFS, Context.MODE_PRIVATE).edit()
      .putString(KEY_REQUEST_ID, requestId)
      .putString(KEY_STATUS, status)
      .putString(KEY_KIND, kind)
      .putString(KEY_TEXT, text)
      .putString(KEY_URI, uri)
      .putString(KEY_MIME_TYPE, mimeType)
      .putString(KEY_ORIGINAL_NAME, originalName)
      .putString(KEY_ERROR, error)
      .commit()
  }

  private fun extractText(intent: Intent): String? {
    val raw = intent.getCharSequenceExtra(Intent.EXTRA_TEXT)?.toString()
      ?: intent.getStringExtra(Intent.EXTRA_HTML_TEXT)
      ?: return null
    val text = raw.trim()
    return text.take(MAX_TEXT_CHARS).takeIf { it.isNotEmpty() }
  }

  @Suppress("DEPRECATION")
  private fun extractUri(intent: Intent): Uri? {
    val direct = if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.TIRAMISU) {
      intent.getParcelableExtra(Intent.EXTRA_STREAM, Uri::class.java)
    } else {
      intent.getParcelableExtra(Intent.EXTRA_STREAM) as? Uri
    }
    if (direct != null) return direct

    val firstMultiple = if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.TIRAMISU) {
      intent.getParcelableArrayListExtra(Intent.EXTRA_STREAM, Uri::class.java)?.firstOrNull()
    } else {
      intent.getParcelableArrayListExtra<Uri>(Intent.EXTRA_STREAM)?.firstOrNull()
    }
    return firstMultiple ?: intent.clipData?.takeIf { it.itemCount > 0 }?.getItemAt(0)?.uri
  }

  private fun displayName(resolver: ContentResolver, uri: Uri): String? {
    var cursor: Cursor? = null
    try {
      cursor = resolver.query(uri, arrayOf(OpenableColumns.DISPLAY_NAME), null, null, null)
      if (cursor?.moveToFirst() == true) {
        val column = cursor.getColumnIndex(OpenableColumns.DISPLAY_NAME)
        if (column >= 0) return cursor.getString(column)
      }
    } finally {
      cursor?.close()
    }
    return null
  }

  private fun copyToCache(context: Context, uri: Uri, mimeType: String): String {
    val directory = File(context.cacheDir, "vass-shared-files").apply { mkdirs() }
    val file = File(directory, "share-${UUID.randomUUID()}.${extensionFor(mimeType)}")
    try {
      context.contentResolver.openInputStream(uri)?.use { input ->
        FileOutputStream(file).use { output ->
          val buffer = ByteArray(DEFAULT_BUFFER_SIZE)
          var total = 0L
          while (true) {
            val count = input.read(buffer)
            if (count < 0) break
            total += count
            if (total > MAX_BYTES) throw IllegalArgumentException("Shared attachment exceeds upload limit")
            output.write(buffer, 0, count)
          }
        }
      } ?: throw IllegalStateException("Shared attachment stream is unavailable")
      if (file.length() == 0L) throw IllegalStateException("Shared attachment is empty")
      return "file://${file.absolutePath}"
    } catch (exception: Exception) {
      file.delete()
      throw exception
    }
  }

  private fun extensionFor(mimeType: String): String = when (mimeType) {
    "image/jpeg" -> "jpg"
    "image/png" -> "png"
    "image/webp" -> "webp"
    "application/pdf" -> "pdf"
    else -> "bin"
  }

  private fun normalizeMimeType(rawMimeType: String?): String {
    val mimeType = rawMimeType?.substringBefore(';')?.trim()?.lowercase()
    return if (mimeType?.matches(Regex("[a-z0-9!#$&^_.+-]+/[a-z0-9!#$&^_.+-]+")) == true) {
      mimeType
    } else {
      "application/octet-stream"
    }
  }
}
