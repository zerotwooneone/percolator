using System.Security.Cryptography.X509Certificates;

namespace Percolator.Identity.Model;

public record Certificate(X509Certificate2 Value);
