type Props = { name: string; category: string; large?: boolean };

// Decorative illustrations until the catalog supports uploaded product photos.
// Match names first so products in a broad category still get suitable artwork.
export function ProductArtwork({ name, category, large = false }: Props) {
  const text = name.toLowerCase();
  const kind = /headphone/.test(text)
    ? 'headphones'
    : /mouse/.test(text)
      ? 'mouse'
      : /camera/.test(text)
        ? 'camera'
        : /bottle/.test(text)
          ? 'bottle'
          : /backpack|bag/.test(text)
            ? 'bag'
            : /keyboard/.test(text)
              ? 'keyboard'
              : /notebook|journal/.test(text)
                ? 'notebook'
                : /mug|cup/.test(text)
                  ? 'mug'
                  : /speaker/.test(text) || category.toLowerCase() === 'audio'
                    ? 'speaker'
                    : 'package';
  return (
    <div className={`product-art${large ? ' large' : ''} product-art--${kind}`} aria-hidden="true">
      <svg
        viewBox="0 0 240 160"
        fill="none"
        stroke="currentColor"
        strokeWidth="3"
        strokeLinecap="round"
        strokeLinejoin="round"
      >
        <ellipse cx="120" cy="137" rx="58" ry="7" fill="currentColor" opacity=".09" stroke="none" />
        {kind === 'headphones' && (
          <>
            <path d="M70 87V72a50 50 0 0 1 100 0v15" strokeWidth="12" opacity=".22" />
            <path d="M70 87V72a50 50 0 0 1 100 0v15" />
            <rect x="59" y="77" width="28" height="51" rx="12" fill="white" />
            <rect x="153" y="77" width="28" height="51" rx="12" fill="white" />
            <path d="M79 88v29m82-29v29" opacity=".45" />
          </>
        )}
        {kind === 'mouse' && (
          <>
            <rect x="86" y="30" width="68" height="102" rx="33" fill="white" />
            <path d="M120 31v40M87 76h66" opacity=".45" />
            <rect x="116" y="44" width="8" height="20" rx="4" fill="currentColor" stroke="none" />
          </>
        )}
        {kind === 'camera' && (
          <>
            <path d="m95 55 8-14h34l8 14" fill="white" />
            <rect x="51" y="55" width="138" height="72" rx="12" fill="white" />
            <path d="M51 75h138" opacity=".25" />
            <circle cx="124" cy="91" r="28" fill="currentColor" opacity=".14" />
            <circle cx="124" cy="91" r="20" />
            <circle cx="124" cy="91" r="10" opacity=".5" />
            <rect x="164" y="65" width="13" height="7" rx="2" fill="currentColor" stroke="none" />
          </>
        )}
        {kind === 'bottle' && (
          <>
            <rect x="105" y="23" width="30" height="17" rx="5" fill="white" />
            <path
              d="M105 40v11l-12 16v56a9 9 0 0 0 9 9h36a9 9 0 0 0 9-9V67l-12-16V40"
              fill="white"
            />
            <path d="M94 78h52v33H94" fill="currentColor" opacity=".14" stroke="none" />
            <path d="M105 70v48" opacity=".35" />
          </>
        )}
        {kind === 'bag' && (
          <>
            <path d="M106 40v-8a14 14 0 0 1 28 0v8" />
            <rect x="80" y="40" width="80" height="94" rx="24" fill="white" />
            <path d="M91 70h58" opacity=".4" />
            <rect x="91" y="86" width="58" height="34" rx="9" fill="currentColor" opacity=".12" />
            <path d="M95 96h50m-5 0v7" />
          </>
        )}
        {kind === 'keyboard' && (
          <>
            <rect x="36" y="56" width="168" height="62" rx="9" fill="white" />
            <path
              d="M49 71h142M49 84h142M49 97h24m12 0h70m12 0h24"
              strokeWidth="6"
              strokeDasharray="5 8"
              opacity=".45"
            />
          </>
        )}
        {kind === 'notebook' && (
          <>
            <rect x="77" y="29" width="88" height="106" rx="6" fill="white" />
            <path d="M90 29v106M151 30v105" opacity=".4" />
            <path d="M106 61h28m-28 13h20" />
          </>
        )}
        {kind === 'mug' && (
          <>
            <path d="M154 64h12a20 20 0 0 1 0 40h-12" strokeWidth="7" />
            <path d="M76 58h78v53a20 20 0 0 1-20 20H96a20 20 0 0 1-20-20Z" fill="white" />
            <path d="M96 40v-9m19 9V25m19 15v-9" opacity=".4" />
          </>
        )}
        {kind === 'speaker' && (
          <>
            <rect x="86" y="26" width="68" height="108" rx="15" fill="white" />
            <circle cx="120" cy="57" r="12" />
            <circle cx="120" cy="100" r="23" fill="currentColor" opacity=".12" />
            <circle cx="120" cy="100" r="13" />
          </>
        )}
        {kind === 'package' && (
          <>
            <path d="m68 54 52-23 52 23v64l-52 23-52-23Z" fill="white" />
            <path d="m68 54 52 24 52-24m-52 24v63M95 42l51 24v21" />
          </>
        )}
      </svg>
    </div>
  );
}
