# Historical schema compatibility

Shipments and returns have been removed from the application. These three entity
classes and their ShopDbContext mappings remain only to keep the EF model
compatible with the existing migration snapshot and preserve stored data.
Their original namespaces are intentional: EF uses the full entity names.

No endpoints, services, consumers, or UI use these entities. Historical migrations
remain unchanged. No database tables or records are deleted by this removal.
