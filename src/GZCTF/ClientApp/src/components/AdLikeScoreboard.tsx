import {
  alpha,
  Avatar,
  Button,
  Grid,
  Group,
  type MantineTheme,
  Select,
  Stack,
  Table,
  Text,
  TextInput,
  useMantineColorScheme,
  useMantineTheme,
} from '@mantine/core'
import { useDebouncedValue } from '@mantine/hooks'
import { mdiAccountGroup, mdiCrosshairsGps, mdiMagnify } from '@mdi/js'
import { Icon } from '@mdi/react'
import cx from 'clsx'
import { CSSProperties, FC, ReactNode, useEffect, useMemo, useState } from 'react'
import { useTranslation } from 'react-i18next'
import { ScrollingText } from '@Components/ScrollingText'
import { useChallengeCategoryLabelMap } from '@Utils/Shared'
import { ChallengeCategory } from '@Api'
import misc from '@Styles/Misc.module.css'
import classes from '@Styles/ScoreboardTable.module.css'

/**
 * Shared scaffolding for the two "A&D-like" boards — AdScoreboardTable and
 * KothScoreboardTable. They render the same Paper / toolbar / pinned-left
 * columns / colored category tier / highlight / footer-pagination and differ
 * only in their per-challenge (A&D: 4 metrics) vs per-hill (KotH: 2 metrics)
 * columns, which each board keeps inline. The jeopardy ScoreboardTable is NOT
 * shared here — it has a different column model (numeric-id divisions, a detail
 * modal, blood bonuses, team avatars, wider pinned Widths).
 */

// Pinned-left columns: [Rank overall, Rank division, Team, count, Total].
// Same cumulative-sticky math both boards used; jeopardy uses different widths.
export const AD_LIKE_WIDTHS = [44, 44, 150, 56, 64]
export const adLikeLefts = AD_LIKE_WIDTHS.reduce(
  (acc, cur) => {
    acc.push(acc[acc.length - 1] + cur)
    return acc
  },
  [0]
)

export const ITEM_COUNT_PER_PAGE = 30

/** Compact point format: integers for large values, one decimal for small. */
export const fmtPts = (n: number) => (Math.abs(n) >= 100 ? Math.round(n).toString() : n.toFixed(1))

/** Per-service/hill status dot color, matching the challenge-panel palette. */
export const statusColor = (s?: string | null) => {
  switch (s) {
    case 'Ok':
      return 'teal'
    case 'Mumble':
      return 'yellow'
    case 'Offline':
      return 'red'
    default:
      return 'gray' // InternalError / never-checked
  }
}

/** Row outline + wash applied to the "find my team" target for 2.5s. */
export const adLikeRowHighlight = (highlighted: boolean, theme: MantineTheme): CSSProperties | undefined =>
  highlighted
    ? {
        outline: `2px solid ${theme.colors[theme.primaryColor][4]}`,
        outlineOffset: -2,
        background: alpha(theme.colors[theme.primaryColor][4], 0.12),
        transition: 'background .3s ease, outline .3s ease',
      }
    : undefined

interface AdLikeRow {
  teamName?: string | null
  division?: string | null
}

/**
 * Owns the filter / pagination / division / highlight state both boards share:
 * keyword (debounced 400ms) + division filter, page reset on filter change,
 * row-window slicing, and the "find my team" jump-and-flash handler. Generic
 * over the row type so each board keeps its concrete row fields.
 */
export const useAdLikeScoreboardState = <T extends AdLikeRow>(
  rows: T[] | undefined,
  myTeamName: string | null
) => {
  const [activePage, setPage] = useState(1)
  const [divisionName, setDivisionName] = useState<string | null>(null)
  const [keyword, setKeyword] = useState('')
  const [debouncedKeyword] = useDebouncedValue(keyword, 400)
  const [highlightedTeam, setHighlightedTeam] = useState<string | null>(null)

  // Neither A&D nor KotH endpoints expose a divisions array (unlike jeopardy),
  // so derive the filter options from the rows themselves.
  const divisionOptions = useMemo(() => {
    if (!rows) return []
    const seen = new Set<string>()
    const out: { value: string; label: string }[] = []
    for (const row of rows) {
      if (row.division && !seen.has(row.division)) {
        seen.add(row.division)
        out.push({ value: row.division, label: row.division })
      }
    }
    return out
  }, [rows])

  const selectValue = divisionName ?? 'all'
  const hasDivisionFilter = divisionOptions.length > 0
  const allRank = divisionName === null

  const filteredList = useMemo(() => {
    if (!rows) return []
    const kw = debouncedKeyword.trim().toLowerCase()
    let list = rows
    if (kw.length > 0) {
      list = list.filter((s) => s.teamName?.toLowerCase().includes(kw))
    } else if (divisionName !== null) {
      list = list.filter((s) => s.division === divisionName)
    }
    return list
  }, [rows, debouncedKeyword, divisionName])

  useEffect(() => {
    setPage(1)
  }, [debouncedKeyword, divisionName])

  const base = (activePage - 1) * ITEM_COUNT_PER_PAGE
  const currentItems = filteredList.slice(base, base + ITEM_COUNT_PER_PAGE)

  const findMyTeam = () => {
    if (!myTeamName) return
    const idx = filteredList.findIndex((it) => it.teamName === myTeamName)
    if (idx < 0) return
    const page = Math.floor(idx / ITEM_COUNT_PER_PAGE) + 1
    setPage(page)
    setHighlightedTeam(myTeamName)
    requestAnimationFrame(() => {
      const el = document.querySelector(`[data-team-name="${CSS.escape(myTeamName)}"]`)
      if (el) el.scrollIntoView({ behavior: 'smooth', block: 'center' })
    })
    setTimeout(() => setHighlightedTeam(null), 2500)
  }

  return {
    activePage,
    setPage,
    divisionName,
    setDivisionName,
    keyword,
    setKeyword,
    highlightedTeam,
    divisionOptions,
    selectValue,
    hasDivisionFilter,
    allRank,
    filteredList,
    base,
    currentItems,
    findMyTeam,
  }
}

interface AdLikeToolbarProps {
  divisionOptions: { value: string; label: string }[]
  selectValue: string
  hasDivisionFilter: boolean
  onDivisionChange: (div: string | null) => void
  myTeamName: string | null
  onFindMyTeam: () => void
  keyword: string
  onKeywordChange: (kw: string) => void
  /** Already-translated "through round N" caption (engine-specific key). */
  latestRoundText: string
}

/** Division select + Find-My-Team button + latest-round caption + search. */
export const AdLikeToolbar: FC<AdLikeToolbarProps> = ({
  divisionOptions,
  selectValue,
  hasDivisionFilter,
  onDivisionChange,
  myTeamName,
  onFindMyTeam,
  keyword,
  onKeywordChange,
  latestRoundText,
}) => {
  const { t } = useTranslation()

  return (
    <Grid>
      <Grid.Col span={3}>
        <Select
          defaultValue="all"
          data={[{ value: 'all', label: t('game.label.score_table.all_teams', 'All teams') }, ...divisionOptions]}
          value={selectValue}
          readOnly={!hasDivisionFilter}
          onChange={(div) => onDivisionChange(!div || div === 'all' ? null : div)}
          leftSection={<Icon path={mdiAccountGroup} size={1} />}
        />
      </Grid.Col>
      <Grid.Col span={4}>
        {myTeamName && (
          <Button
            variant="light"
            leftSection={<Icon path={mdiCrosshairsGps} size={0.9} />}
            onClick={onFindMyTeam}
          >
            {t('game.button.find_my_team', 'Find My Team')}
          </Button>
        )}
      </Grid.Col>
      <Grid.Col span={2}>
        <Group justify="flex-end" gap="xs" h="100%">
          <Text size="xs" c="dimmed">
            {latestRoundText}
          </Text>
        </Group>
      </Grid.Col>
      <Grid.Col span={3}>
        <TextInput
          placeholder={t('game.placeholder.search_team', 'Search Team')}
          value={keyword}
          onChange={(e) => onKeywordChange(e.currentTarget.value)}
          leftSection={<Icon path={mdiMagnify} size={1} />}
        />
      </Grid.Col>
    </Grid>
  )
}

/** Empty sticky placeholders for the pinned columns in the upper header tiers. */
export const AdLikeHiddenCols: FC = () => (
  <>
    {[...Array(5).keys()].map((i) => (
      <Table.Th
        key={`hidden-${i}`}
        className={classes.left}
        style={{ left: adLikeLefts[i], width: AD_LIKE_WIDTHS[i], minWidth: AD_LIKE_WIDTHS[i], maxWidth: AD_LIKE_WIDTHS[i] }}
      >
        &nbsp;
      </Table.Th>
    ))}
  </>
)

interface AdLikeCategoryHeaderRowProps {
  groups: { category: string; items: unknown[] }[]
  /** Sub-columns each item spans (A&D: 4 metrics, KotH: 2). */
  subColsPerItem: number
}

/**
 * Tier-1 colored category band — one cell per category group spanning its
 * items' sub-columns — plus the leading pinned placeholders and the trailing
 * flexible spacer that soaks up surplus width at min-width:100%.
 */
export const AdLikeCategoryHeaderRow: FC<AdLikeCategoryHeaderRowProps> = ({ groups, subColsPerItem }) => {
  const theme = useMantineTheme()
  const { colorScheme } = useMantineColorScheme()
  const categoryLabelMap = useChallengeCategoryLabelMap()

  return (
    <Table.Tr className={misc.noBorder}>
      <AdLikeHiddenCols />
      {groups.map((grp) => {
        const cate = categoryLabelMap.get(grp.category as ChallengeCategory)
        return (
          <Table.Th
            key={grp.category}
            colSpan={grp.items.length * subColsPerItem}
            className={classes.groupStart}
            h="2.4rem"
            style={
              cate
                ? {
                    backgroundColor: alpha(
                      theme.colors[cate.color][colorScheme === 'dark' ? 8 : 6],
                      colorScheme === 'dark' ? 0.15 : 0.2
                    ),
                  }
                : undefined
            }
          >
            <Group gap={4} wrap="nowrap" justify="center" w="100%">
              {cate && (
                <Icon path={cate.icon} size={0.8} color={theme.colors[cate.color][colorScheme === 'dark' ? 8 : 6]} />
              )}
              <Text c={cate?.color} className={classes.text} ff="text" fz="xs">
                {grp.category}
              </Text>
            </Group>
          </Table.Th>
        )
      })}
      {/* Flexible spacer — soaks up surplus when forced to min-width:100% with
          few challenges; spans all three header rows. */}
      <Table.Th rowSpan={3} aria-hidden />
    </Table.Tr>
  )
}

interface AdLikePinnedHeaderCellsProps {
  /** 4th pinned label — engine-specific ("Captures" / "Ticks"). */
  countLabel: string
  /** 5th pinned label — engine-specific ("Total"). */
  totalLabel: string
}

/** Tier-3 pinned column labels: Rank / Division / Team / count / Total. */
export const AdLikePinnedHeaderCells: FC<AdLikePinnedHeaderCellsProps> = ({ countLabel, totalLabel }) => {
  const { t } = useTranslation()

  const labels = [
    t('game.label.score_table.rank_total', 'Rank'),
    t('game.label.score_table.rank_division', 'Division'),
    t('common.label.team', 'Team'),
    countLabel,
    totalLabel,
  ]

  return (
    <>
      {labels.map((header, idx) => (
        <Table.Th key={idx} className={cx(classes.left, classes.header)} style={{ left: adLikeLefts[idx] }}>
          {header}
        </Table.Th>
      ))}
    </>
  )
}

interface AdLikePinnedRowCellsProps {
  rank: number
  teamName?: string | null
  division?: string | null
  total: number
  allRank: boolean
  tableRank: number
  /** 4th pinned cell value — engine-specific (captures / ticks held). */
  countValue: ReactNode
}

/** Pinned-left body cells: rank, division rank, team avatar+name, count, total. */
export const AdLikePinnedRowCells: FC<AdLikePinnedRowCellsProps> = ({
  rank,
  teamName,
  division,
  total,
  allRank,
  tableRank,
  countValue,
}) => {
  const theme = useMantineTheme()

  return (
    <>
      <Table.Td className={cx(classes.mono, classes.left)} style={{ left: adLikeLefts[0] }}>
        {rank || '-'}
      </Table.Td>
      <Table.Td className={cx(classes.mono, classes.left)} style={{ left: adLikeLefts[1] }}>
        {allRank ? rank : tableRank}
      </Table.Td>
      <Table.Td className={classes.left} style={{ left: adLikeLefts[2] }}>
        <Group justify="left" gap={5} wrap="nowrap" maw={AD_LIKE_WIDTHS[2] - 10}>
          <Avatar imageProps={{ loading: 'lazy' }} alt="avatar" radius="xl" size={30} color={theme.primaryColor}>
            {teamName?.slice(0, 1) ?? 'T'}
          </Avatar>
          <Stack gap={0} h="2.5rem" justify="center" w={AD_LIKE_WIDTHS[2] - 45}>
            <ScrollingText size="sm" text={teamName || ''} />
            {!!division && (
              <Text size="xs" c="dimmed" ta="start" truncate className={classes.text}>
                {division}
              </Text>
            )}
          </Stack>
        </Group>
      </Table.Td>
      <Table.Td className={cx(classes.mono, classes.left)} style={{ left: adLikeLefts[3] }}>
        {countValue}
      </Table.Td>
      <Table.Td className={cx(classes.mono, classes.left)} style={{ left: adLikeLefts[4] }}>
        {total.toFixed(1)}
      </Table.Td>
    </>
  )
}
