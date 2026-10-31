using Declarad.Directory;

namespace Declarad.Tests
{
    public class DirectoryTests
    {
        [Theory]
        [InlineData("Doe, John", "Doe\\, John")]
        [InlineData(" lead", "\\ lead")]
        [InlineData("trail ", "trail\\ ")]
        [InlineData("#hash", "\\#hash")]
        [InlineData("a+b=c;d<e>\"f\\", "a\\+b\\=c\\;d\\<e\\>\\\"f\\\\")]
        public void EscapesAndParsesRdnValues(string value, string escaped)
        {
            Assert.Equal(escaped, Dn.EscapeValue(value));
            var rdn = Dn.First(Dn.Combine("CN", value, "DC=corp,DC=local"));
            Assert.Equal(value, rdn.Value);
        }

        [Fact]
        public void HandlesHexEscapesAndNormalization()
        {
            Assert.Equal("Doe, John", Dn.First("CN=Doe\\2C John,DC=x").Value);
            Assert.Equal("Müller", Dn.First("CN=M\\C3\\BCller,DC=x").Value);
            Assert.True(Dn.AreEqual("cn=Doe\\2C John, ou=Sales ,dc=Corp,DC=local", "CN=doe\\, john,OU=sales,DC=corp,DC=local"));
            Assert.False(Dn.AreEqual("CN=a,DC=x", "CN=b,DC=x"));
        }

        [Fact]
        public void ComputesStructure()
        {
            Assert.Equal("OU=Sales,DC=corp,DC=local", Dn.Parent("CN=x\\,y,OU=Sales,DC=corp,DC=local"));
            Assert.True(Dn.IsDescendantOf("CN=x,OU=Sales,DC=corp,DC=local", "dc=corp,dc=local"));
            Assert.False(Dn.IsDescendantOf("DC=corp,DC=local", "DC=corp,DC=local"));
            Assert.False(Dn.IsDescendantOf("OU=XSales,DC=corp", "OU=Sales,DC=corp"));
            Assert.Equal("DC=corp,DC=local", Dn.FromDomain("corp.local"));
            Assert.Equal("corp.local", Dn.ToDomain("OU=a,DC=corp,DC=local"));
            Assert.Equal(3, Dn.Depth("OU=a,DC=corp,DC=local"));
            Assert.False(Dn.IsValid("not a dn"));
            Assert.False(Dn.IsValid("OU=a,"));
            Assert.Equal("\\2a\\28x\\29\\5c", Dn.EscapeFilterValue("*(x)\\"));
        }

        [Fact]
        public void TogglesDeletionProtectionInSecurityDescriptor()
        {
            var original = InMemoryDirectory.DefaultSd();
            Assert.False(SecurityDescriptor.IsProtectedFromDeletion(original));

            var protectedSd = SecurityDescriptor.SetProtectedFromDeletion(original, true);
            Assert.True(SecurityDescriptor.IsProtectedFromDeletion(protectedSd));
            Assert.Equal(original.Length + 20, protectedSd.Length);

            // Idempotent: protecting twice keeps a single deny ACE.
            Assert.Equal(protectedSd, SecurityDescriptor.SetProtectedFromDeletion(protectedSd, true));

            var unprotectedSd = SecurityDescriptor.SetProtectedFromDeletion(protectedSd, false);
            Assert.False(SecurityDescriptor.IsProtectedFromDeletion(unprotectedSd));
            Assert.Equal(original, unprotectedSd);
        }

        [Fact]
        public void SecurityDescriptorWithoutOwnerOrGroupIsSupported()
        {
            // A DACL-only descriptor, as AD returns when only DACL_SECURITY_INFORMATION is requested.
            var full = InMemoryDirectory.DefaultSd();
            var daclOnly = full.Take(20 + 28).ToArray();
            Array.Clear(daclOnly, 4, 8);
            var result = SecurityDescriptor.SetProtectedFromDeletion(daclOnly, true);
            Assert.True(SecurityDescriptor.IsProtectedFromDeletion(result));
            Assert.Equal(0, BitConverter.ToInt32(result, 4));
        }
    }
}
