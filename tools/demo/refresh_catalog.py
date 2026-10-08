"""Replace retained local verification metadata with demo content, preserving stock/history.

Dry-run by default. --apply writes a JSON backup before updating through the normal
catalog CLI (SQL + Outbox), never through direct SQL or Elasticsearch writes.
"""
import argparse
import datetime
import json
import re
import subprocess
import time
from pathlib import Path
from urllib.request import urlopen
from urllib.error import HTTPError

ROOT = Path(__file__).resolve().parents[2]
BASE_URL = 'http://127.0.0.1:5088'
TEMPLATES = [
    ('Everyday Notebook', 'Stationery', 'A compact ruled notebook for notes, plans, and everyday ideas.'),
    ('Ceramic Coffee Mug', 'Home', 'A comfortable ceramic mug for your morning coffee or evening tea.'),
    ('Portable Speaker', 'Audio', 'A compact speaker for music at your desk or on the go.'),
    ('Wireless Keyboard', 'Accessories', 'A slim keyboard with a comfortable layout for everyday work.'),
    ('Insulated Water Bottle', 'Outdoor', 'A reusable insulated bottle for daily commutes and weekend walks.'),
    ('Everyday Backpack', 'Bags', 'A practical backpack with room for your daily essentials.'),
    ('Pocket Journal', 'Stationery', 'A lightweight journal for thoughts, sketches, and reminders.'),
    ('Travel Coffee Cup', 'Home', 'A reusable cup made for coffee breaks away from home.'),
    ('Desk Speaker', 'Audio', 'A simple desktop speaker for a more enjoyable workspace.'),
    ('Compact Wireless Mouse', 'Accessories', 'A comfortable wireless mouse for laptops and desktop computers.'),
    ('Weekend Backpack', 'Bags', 'An easy-to-carry backpack for short trips and weekend adventures.'),
    ('Studio Notebook', 'Stationery', 'A versatile notebook for creative projects and daily planning.'),
]
COLORS = ['Sage', 'Sand', 'Slate', 'Navy', 'Terracotta', 'Olive', 'Cream', 'Charcoal', 'Sky']


def is_fixture(product):
    category, name = product['category'], product['name']
    return (
        category == 'Verification'
        or bool(re.fullmatch(r'(?:Catalog|A|B)-[0-9a-f]{32}', category))
        or (category == 'Browser' and bool(re.fullmatch(
            r'(?:Browser item|UI product) [0-9a-f]{8}(?:-[0-9a-f]{4}){3}-[0-9a-f]{12}', name)))
    )


def replacement(product, index):
    if product['id'] == 2 and product['name'] == 'Wireless Mouse' and product['description'].startswith('Redis test '):
        return {**product, 'description': 'Compact wireless mouse for laptops and desktop computers'}
    if re.fullmatch(r'Demo Camera \d+', product['name']) and product['category'] == 'Photo':
        return {**product, 'name': 'Compact Digital Camera', 'description': 'A compact wide-angle camera for capturing everyday moments.'}
    if re.fullmatch(r'Recovery Camera \d+', product['name']) and product['category'] == 'Photo':
        return {**product, 'name': 'Travel Digital Camera', 'description': 'A lightweight camera for weekend adventures and everyday photography.'}
    if not is_fixture(product):
        return None
    name, category, description = TEMPLATES[index % len(TEMPLATES)]
    color = COLORS[(index // len(TEMPLATES)) % len(COLORS)]
    return {**product, 'name': f'{name} — {color}', 'category': category, 'description': description}


def read(path):
    for attempt in range(4):
        try:
            with urlopen(BASE_URL + path, timeout=30) as response:
                return json.load(response)
        except HTTPError as error:
            if error.code != 429 or attempt == 3:
                raise
            delay = max(1, min(60, int(error.headers.get('Retry-After', '60'))))
            error.close()
            print(f'Catalog read limit reached; retrying in {delay}s.', flush=True)
            time.sleep(delay)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--apply', action='store_true', help='Back up and update recognized local demo/test products.')
    args = parser.parse_args()
    products, page = [], 1
    while True:
        result = read(f'/products?page={page}&pageSize=50')
        products.extend(result['products'])
        if len(products) >= result['total']:
            break
        page += 1
    changes = []
    index = 0
    for product in products:
        updated = replacement(product, index)
        if updated is not None:
            changes.append((product, updated))
        # Count completed variants too so an interrupted refresh keeps the same
        # names for the remaining fixtures when it is run again.
        if is_fixture(product) or product['name'] in {
            f'{name} — {color}' for name, _, _ in TEMPLATES for color in COLORS
        }:
            index += 1
    print(f'{len(changes)} recognized products to refresh; {len(products)-len(changes)} other products unchanged.')
    for original, updated in changes[:12]:
        print(f"  {original['id']}: {original['name']} -> {updated['name']}")
    if not args.apply or not changes:
        print('Dry-run only. Use --apply to save the backup and update the catalog.' if not args.apply else 'Nothing to update.')
        return
    # Capture complete public details, including price and current stock, before mutation.
    snapshot = [read(f"/products/{original['id']}") for original, _ in changes]
    for (original, _), current in zip(changes, snapshot):
        if any(current[key] != original[key] for key in ('name', 'description', 'category', 'priceCents')):
            raise RuntimeError('Catalog changed during planning. Run the script again.')
    stamp = datetime.datetime.now(datetime.timezone.utc).strftime('%Y%m%dT%H%M%S%fZ')
    folder = ROOT / '.superpowers' / 'demo-catalog'
    folder.mkdir(parents=True, exist_ok=True)
    backup = folder / f'{stamp}.json'
    backup.write_text(json.dumps({'baseUrl': BASE_URL, 'products': snapshot}, indent=2) + '\n')
    print(f'Original metadata backup: {backup}', flush=True)
    for count, (original, updated) in enumerate(changes, 1):
        command = ['docker', 'compose', 'exec', '-T', 'ecommerce', 'dotnet', 'Ecommerce.Api.dll',
                   '--update-product', str(original['id']), updated['name'], updated['description'],
                   updated['category'], str(original['priceCents'])]
        subprocess.run(command, cwd=ROOT, check=True, stdout=subprocess.DEVNULL)
        current = read(f"/products/{original['id']}")
        if any(current[key] != updated[key] for key in ('name', 'description', 'category', 'priceCents')):
            raise RuntimeError(f"Product {original['id']} did not match the intended update.")
        if count % 20 == 0:
            print(f'Updated {count}/{len(changes)} products.', flush=True)
    print(f'Refreshed {len(changes)} products. IDs, prices, stock and order links preserved; search follows through Outbox.')


if __name__ == '__main__':
    main()
