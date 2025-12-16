# BydTools.Utils

Common utilities library for BydTools projects, containing shared functionality used across VFS and PCK tools.

## Components

### SparkBuffer
SparkBuffer parsing and decryption utilities:
- `SparkBufferDumper` - Convert SparkBuffer binary files to JSON format
- `SparkManager` - Manage type definitions for SparkBuffer parsing
- `BeanType`, `EnumType` - Type definitions for SparkBuffer structures
- `BinaryReaderExtensions` - Extension methods for reading SparkBuffer data

### Crypto
Cryptography utilities:
- `CSChaCha20` - ChaCha20 encryption/decryption implementation with SIMD support

### Extensions
Common extension methods:
- `StreamExtensions` - Stream copying utilities
- `UInt128Extensions` - UInt128 hex string conversion methods

## Usage

Add a reference to this project in your `.csproj`:

```xml
<ItemGroup>
  <ProjectReference Include="..\BydTools.Utils\BydTools.Utils.csproj" />
</ItemGroup>
```

Then import the namespaces you need:

```csharp
using BydTools.Utils.SparkBuffer;
using BydTools.Utils.Crypto;
using BydTools.Utils.Extensions;
```

### SparkBuffer Example

```csharp
// Decrypt SparkBuffer data to JSON
byte[] sparkBufferData = File.ReadAllBytes("config.bytes");
string json = SparkBufferDumper.Decrypt(sparkBufferData);
File.WriteAllText("config.json", json);
```

### ChaCha20 Example

```csharp
byte[] key = Convert.FromBase64String("your-base64-key");
byte[] nonce = new byte[12]; // 12-byte nonce
using var chacha = new CSChaCha20(key, nonce, 1);
byte[] encrypted = chacha.EncryptBytes(plaintext);
byte[] decrypted = chacha.DecryptBytes(encrypted);
```

