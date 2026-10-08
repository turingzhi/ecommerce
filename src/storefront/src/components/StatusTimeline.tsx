import type { Tracking } from '../api/types';
export function StatusTimeline({ tracking }: { tracking: Tracking }) {
  return (
    <section>
      <h2>Shipping</h2>
      {tracking.shipment ? (
        <>
          <p>
            {tracking.shipment.status}
            {tracking.shipment.trackingNumber
              ? ` &#183; Tracking: ${tracking.shipment.trackingNumber}`
              : ''}
          </p>
          <ol className="timeline">
            {tracking.history.map((h, i) => (
              <li key={i}>
                <strong>{h.toStatus}</strong> <time>{new Date(h.occurredAt).toLocaleString()}</time>
              </li>
            ))}
          </ol>
        </>
      ) : (
        <p>No shipment yet. Refresh after payment processing.</p>
      )}
    </section>
  );
}
