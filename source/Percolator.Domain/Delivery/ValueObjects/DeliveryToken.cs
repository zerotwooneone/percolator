using Percolator.SourceGenerators;

namespace Percolator.Domain.Delivery.ValueObjects;

[ByteArray(length: 32)]
public sealed partial record DeliveryToken;
