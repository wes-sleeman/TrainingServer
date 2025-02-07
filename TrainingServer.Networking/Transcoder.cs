﻿namespace TrainingServer.Networking;

using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;

using static System.Text.Encoding;

/// <summary>
/// Provides symmetric and asymmetric encryption capabilities for managing secure communications with multiple recipients.
/// </summary>
[Obsolete("This is likely going to be useful in the future, but for now it's just in the way.")]
public class Transcoder
{
	readonly RSA _asymmetricProvider = RSA.Create();
	readonly Aes _symmetricProvider = Aes.Create();
	Dictionary<Guid, Aes>? _symmetricKeys = null;

	/// <summary>Gets the public key for the current instance's asymmetric encryption engine.</summary>
	public RSAParameters GetAsymmetricKey() => _asymmetricProvider.ExportParameters(false);

	/// <summary>Overwrites the keys for the current instance's asymmetric encryption engine.</summary>
	/// <param name="serverKeyParams">The <see cref="RSAParameters"/> containing the new asymmetric key(s).</param>
	public void LoadAsymmetricKey(RSAParameters serverKeyParams) => _asymmetricProvider.ImportParameters(serverKeyParams);

	/// <summary>Performs RSA encryption using the current instance's public key.</summary>
	/// <param name="data">The data to be encrypted.</param>
	/// <returns>The cyphered data.</returns>
	public byte[] AsymmetricEncrypt(byte[] data) => _asymmetricProvider.Encrypt(data, RSAEncryptionPadding.OaepSHA256);

	/// <summary>Performs RSA decryption using the current instance's private key.</summary>
	/// <param name="data">The cyphered data to be decrypted.</param>
	/// <returns>The plaintext data.</returns>
	public byte[] AsymmetricDecrypt(byte[] data) => _asymmetricProvider.Decrypt(data, RSAEncryptionPadding.OaepSHA256);

	/// <summary>Registers a new symmetric encryption tunnel to the current instance.</summary>
	/// <param name="recipient">The <see cref="Guid"/> associated with the tunnel.</param>
	/// <param name="key">The symmetric AES key securing the tunnel.</param>
	public void RegisterKey(Guid recipient, byte[] key)
	{
		_symmetricKeys ??= [];

		_symmetricKeys[recipient] = Aes.Create();
		_symmetricKeys[recipient].Key = key;
	}

	/// <summary>Registers a new symmetric encryption tunnel to the current instance.</summary>
	/// <param name="recipient">The <see cref="Guid"/> associated with the tunnel.</param>
	/// <param name="key">The symmetric AES key securing the tunnel.</param>
	public void RegisterSecondaryRecipient(Guid recipient, Guid relay)
	{
		_symmetricKeys ??= [];

		_symmetricKeys[recipient] = _symmetricKeys[relay];
	}

	/// <summary>Unregisters a symmetric encryption tunnal from the current instance.</summary>
	/// <param name="recipient">The <see cref="Guid"/> associated with the tunnel.</param>
	/// <returns><see langword="true"/> if the tunnel was successfully deregistered, otherwise <see langword="false"/>.</returns>
	public bool TryUnregister(Guid recipient) => _symmetricKeys?.Remove(recipient) ?? throw new InvalidOperationException("Cannot deregister a client secure tunnel when in client mode.");

	/// <summary>Encrypts the given <see cref="string"/> using the key asssociated with the provided <see cref="Guid"/>'s tunnel.</summary>
	/// <param name="recipient">The <see cref="Guid"/> associated with a registered tunnel.</param>
	/// <param name="data">The <see cref="string"/> to be encrypted.</param>
	/// <returns>The encryped message ready to be sent over the identified tunnel.</returns>
	/// <exception cref="ArgumentException">The given <paramref name="recipient"/> has not been registered. <seealso cref="RegisterKey(Guid, byte[])"/></exception>
	public byte[] SymmetricEncryptString(Guid recipient, string data) => SymmetricEncrypt(recipient, UTF8.GetBytes(data));

	/// <summary>Encrypts the given array of <see cref="byte"/>s using the key asssociated with the provided <see cref="Guid"/>'s tunnel.</summary>
	/// <param name="recipient">The <see cref="Guid"/> associated with a registered tunnel.</param>
	/// <param name="data">The buffer to be encrypted.</param>
	/// <returns>The encryped message ready to be sent over the identified tunnel.</returns>
	/// <exception cref="ArgumentException">The given <paramref name="recipient"/> has not been registered. <seealso cref="RegisterKey(Guid, byte[])"/></exception>
	public byte[] SymmetricEncrypt(Guid recipient, byte[] data)
	{
		Aes? crypt = _symmetricProvider;

		if (_symmetricKeys is not null && !_symmetricKeys.TryGetValue(recipient, out crypt))
			// Pick a random key.
			// throw new ArgumentException($"No tunnel set up with {recipient}.", nameof(recipient));
			crypt = _symmetricKeys.Values.First();

		crypt.GenerateIV();

		return [.. crypt.IV, .. crypt.EncryptCbc(data, crypt.IV)];
	}

	/// <summary>Decrypts a text message received through an encrypted tunnel.</summary>
	/// <param name="sender">The <see cref="Guid"/> associated with a registered tunnel.</param>
	/// <param name="data">The ciphered message buffer.</param>
	/// <returns>The decrypted plaintext.</returns>
	/// <exception cref="ArgumentException">The given <paramref name="sender"/> has not been registered. <seealso cref="RegisterKey(Guid, byte[])"/></exception>
	public string SymmetricDecryptString(Guid sender, byte[] data) => UTF8.GetString(SymmetricDecrypt(sender, data));

	/// <summary>Decrypts a binary message received through an encrypted tunnel.</summary>
	/// <param name="sender">The <see cref="Guid"/> associated with a registered tunnel.</param>
	/// <param name="data">The ciphered message buffer.</param>
	/// <returns>The decrypted buffer.</returns>
	/// <exception cref="ArgumentException">The given <paramref name="sender"/> has not been registered. <seealso cref="RegisterKey(Guid, byte[])"/></exception>
	public byte[] SymmetricDecrypt(Guid sender, byte[] data)
	{
		Aes? crypt = _symmetricProvider;

		if (_symmetricKeys is not null && !_symmetricKeys.TryGetValue(sender, out crypt))
			// Pick a random key.
			//throw new ArgumentException($"No tunnel set up with {sender}.", nameof(sender));
			crypt = _symmetricKeys.Values.First();

		return crypt.DecryptCbc(data[crypt.IV.Length..], data[..crypt.IV.Length]);
	}

	/// <summary>Unpacks a message received from a registered tunnel.</summary>
	/// <param name="message">The received message.</param>
	/// <returns>The timestamp and JSON data encoded in the message.</returns>
	/// <exception cref="ArgumentException">The message was not valid.</exception>
	/// <exception cref="CryptographicException">The message could not be sensibly decoded with the given tunnel's key.</exception>
	public (DateTimeOffset Time, Guid Recipient, JsonNode Data) SecureUnpack(string message)
	{
		if (message.Length < 2 || !message.Contains('}'))
			throw new ArgumentException($"Cannot deserialize message {message}.", nameof(message));

		message = message[..(message.IndexOf("data") + message[message.IndexOf("data")..].IndexOf('}') + 1)];

		if (JsonNode.Parse(message) is not JsonObject jo)
			throw new ArgumentException($"Cannot deserialize message {message}.", nameof(message));

		if (jo["r"]?.GetValue<Guid>() is not Guid recipient)
			throw new ArgumentException($"Cannot identify recipient from message {message}.", nameof(message));

		if (jo["s"]?.GetValue<Guid>() is not Guid sender)
			throw new ArgumentException($"Cannot identify sender of message {message}.", nameof(message));

		if (jo["time"]?.GetValue<DateTimeOffset>() is not DateTimeOffset time)
			throw new ArgumentException($"Message {message} is missing required timestamp.", nameof(message));

		if (jo["data"]?.GetValue<string>() is not string cryptedString)
			return (time, recipient, new JsonObject());

		string packet = SymmetricDecryptString(sender, System.Convert.FromBase64String(cryptedString));

		if (JsonNode.Parse(packet) is JsonNode retval)
			return (time, recipient, retval);
		else
			throw new CryptographicException("The given message could not be sensibly decoded.");
	}

	/// <summary>Unpacks the metadata from a received message without decrypting.</summary>
	/// <param name="message">The received message.</param>
	/// <returns>The timestamp and recipient GUID encoded in the message.</returns>
	/// <exception cref="ArgumentException">The message was not valid.</exception>
	public static (DateTimeOffset Time, Guid Recipeint) DetermineRecipient(string message)
	{
		if (message.Length < 2 || !message.Contains('}'))
			throw new ArgumentException($"Cannot deserialize message {message}.", nameof(message));

		message = message[..(message.IndexOf("data") + message[message.IndexOf("data")..].IndexOf('}') + 1)];

		if (JsonNode.Parse(message) is not JsonObject jo)
			throw new ArgumentException($"Cannot deserialize message {message}.", nameof(message));

		if (jo["r"]?.GetValue<Guid>() is not Guid recipient)
			throw new ArgumentException($"Cannot identify recipient from message {message}.", nameof(message));

		if (jo["s"]?.GetValue<Guid>() is not Guid)
			throw new ArgumentException($"Cannot identify sender of message {message}.", nameof(message));

		if (jo["time"]?.GetValue<DateTimeOffset>() is not DateTimeOffset time)
			throw new ArgumentException($"Message {message} is missing required timestamp.", nameof(message));

		return (time, recipient);
	}

	/// <summary>Packs a message for a given registered tunnel.</summary>
	/// <param name="recipient">The <see cref="Guid"/> of the tunnel through which the message will be sent.</param>
	/// <param name="message">The message to be sent.</param>
	/// <returns>The encoded message.</returns>
	public string SecurePack(Guid sender, Guid recipient, object message)
	{
		string data = JsonSerializer.Serialize(message);
		byte[] packet = SymmetricEncryptString(recipient, data);
		return JsonSerializer.Serialize(new { s = sender, r = recipient, time = DateTimeOffset.Now, data = System.Convert.ToBase64String(packet) });
	}
}