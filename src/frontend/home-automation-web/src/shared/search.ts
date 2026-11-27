// Device search shared by every section of the Devices page. It filters data the page already loaded; it never fetches.

/** Lower-case words of a query, trimmed. An empty list matches everything. */
export function searchTerms(query: string) {
  return query.trim().toLocaleLowerCase().split(/\s+/).filter(Boolean)
}

/** True when every term appears, as part of a word or whole, in at least one of the fields (case-insensitive). */
export function matchesSearch(terms: string[], fields: (string | number | null | undefined)[]) {
  if (terms.length === 0) return true
  const text = fields.filter(field => field !== null && field !== undefined && field !== '').join(' \u0001 ').toLocaleLowerCase()
  return terms.every(term => text.includes(term))
}
