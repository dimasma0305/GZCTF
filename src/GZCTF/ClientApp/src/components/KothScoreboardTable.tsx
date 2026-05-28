import {
  alpha,
  Avatar,
  Badge,
  Box,
  Button,
  Center,
  Grid,
  Group,
  Pagination,
  Paper,
  Select,
  Stack,
  Table,
  Text,
  TextInput,
  Tooltip,
  useMantineColorScheme,
  useMantineTheme,
} from '@mantine/core'
import { useDebouncedValue } from '@mantine/hooks'
import { mdiAccountGroup, mdiCrosshairsGps, mdiCrown, mdiHeartPulse, mdiMagnify, mdiTimerSandComplete } from '@mdi/js'
import { Icon } from '@mdi/react'
import cx from 'clsx'
import { FC, useEffect, useMemo, useState } from 'react'
import { useTranslation } from 'react-i18next'
import { ScrollingText } from '@Components/ScrollingText'
import { useGame, useKothScoreboard, type KothScoreboardHill } from '@Hooks/useGame'
import { useChallengeCategoryLabelMap } from '@Utils/Shared'
import { ChallengeCategory } from '@Api'
import misc from '@Styles/Misc.module.css'
import classes from '@Styles/ScoreboardTable.module.css'

// Same Widths/Lefts cumulative-sticky math as ScoreboardTable + AdScoreboardTable
// so the pinned-left columns visually line up across all three boards.
// Pinned columns: [Rank overall, Rank division, Team, Ticks held, Total].
const Widths = [44, 44, 150, 56, 64]
const Lefts = Widths.reduce(
  (acc, cur) => {
    acc.push(acc[acc.length - 1] + cur)
    return acc
  },
  [0]
)

const ITEM_COUNT_PER_PAGE = 30

// Per-hill sub-columns: hold points + status. Two narrow columns keep the
// table compact regardless of hill count (vs A&D's 4 metrics per service).
const SUBCOL = { pts: 56, status: 56 }
const GROUP_W = SUBCOL.pts + SUBCOL.status

const fmtPts = (n: number) => (Math.abs(n) >= 100 ? Math.round(n).toString() : n.toFixed(1))

const statusColor = (s?: string | null) => {
  switch (s) {
    case 'Ok':
      return 'teal'
    case 'Mumble':
      return 'yellow'
    case 'Offline':
      return 'red'
    default:
      return 'gray'
  }
}

interface KothScoreboardTableProps {
  numId: number
}

/**
 * King of the Hill — dedicated scoreboard. Visually matches AdScoreboardTable
 * (same Paper / toolbar / pinned-left columns / sticky tier-headers / footer
 * legend) and ScoreboardTable's Widths math, so the three boards have a
 * consistent look + the user can switch tabs without re-orienting.
 *
 * Structurally simpler than the A&D board (one metric per hill instead of
 * four), so each hill column is two sub-cells: hold points + functional
 * status badge. Per-hill header carries the "currently held by" pill.
 */
export const KothScoreboardTable: FC<KothScoreboardTableProps> = ({ numId }) => {
  const { t } = useTranslation()
  const theme = useMantineTheme()
  const { colorScheme } = useMantineColorScheme()
  const { kothScoreboard } = useKothScoreboard(numId)
  const { game } = useGame(numId)
  const categoryLabelMap = useChallengeCategoryLabelMap()
  const myTeamName = game?.teamName ?? null

  // Group hills by category — backend returns them already sorted by category
  // (AdScoreboardRepository: OrderBy(c.Category).ThenBy(c.Id)), so contiguous
  // hills with the same category live in one group. Renders the same colored
  // category tier as AdScoreboardTable / ScoreboardTable for parity.
  const hillGroups = useMemo(() => {
    const out: { category: string; items: KothScoreboardHill[] }[] = []
    if (!kothScoreboard) return out
    for (const h of kothScoreboard.hills) {
      const last = out[out.length - 1]
      if (last && last.category === h.category) last.items.push(h)
      else out.push({ category: h.category, items: [h] })
    }
    return out
  }, [kothScoreboard])

  const [activePage, setPage] = useState(1)
  const [divisionName, setDivisionName] = useState<string | null>(null)
  const [keyword, setKeyword] = useState('')
  const [debouncedKeyword] = useDebouncedValue(keyword, 400)
  const [highlightedTeam, setHighlightedTeam] = useState<string | null>(null)

  // KotH scoreboard endpoint doesn't expose a divisions array (mirrors A&D —
  // derive from rows so the filter works on multi-division games).
  const divisionOptions = useMemo(() => {
    if (!kothScoreboard) return []
    const seen = new Set<string>()
    const out: { value: string; label: string }[] = []
    for (const row of kothScoreboard.teams) {
      if (row.division && !seen.has(row.division)) {
        seen.add(row.division)
        out.push({ value: row.division, label: row.division })
      }
    }
    return out
  }, [kothScoreboard])
  const selectValue = divisionName ?? 'all'

  const filteredList = useMemo(() => {
    if (!kothScoreboard?.teams) return []
    const kw = debouncedKeyword.trim().toLowerCase()
    let list = kothScoreboard.teams
    if (kw.length > 0) {
      list = list.filter((s) => s.teamName?.toLowerCase().includes(kw))
    } else if (divisionName !== null) {
      list = list.filter((s) => s.division === divisionName)
    }
    return list
  }, [kothScoreboard, debouncedKeyword, divisionName])

  useEffect(() => {
    setPage(1)
  }, [debouncedKeyword, divisionName])

  const base = (activePage - 1) * ITEM_COUNT_PER_PAGE
  const currentItems = filteredList.slice(base, base + ITEM_COUNT_PER_PAGE)

  const hasDivisionFilter = divisionOptions.length > 0
  const allRank = divisionName === null

  // Empty sticky placeholders for the pinned columns in the tier-1 header row
  // (same trick as the other boards) so pinned-left cells line up across rows.
  const hiddenCol = [...Array(5).keys()].map((i) => (
    <Table.Th
      key={`hidden-${i}`}
      className={classes.left}
      style={{ left: Lefts[i], width: Widths[i], minWidth: Widths[i], maxWidth: Widths[i] }}
    >
      &nbsp;
    </Table.Th>
  ))

  if (!kothScoreboard || kothScoreboard.hills.length === 0) {
    return (
      <Paper shadow="md" p="xl">
        <Stack align="center" gap="xs">
          <Icon path={mdiCrown} size={2.5} color="var(--mantine-color-dimmed)" />
          <Text fw="bold" c="dimmed">
            {!kothScoreboard
              ? t('game.content.scoreboard.koth.loading', 'Loading King of the Hill…')
              : t('game.content.scoreboard.koth.empty.title', 'No hills configured')}
          </Text>
          {kothScoreboard && (
            <Text size="sm" c="dimmed">
              {t('game.content.scoreboard.koth.empty.description',
                'An organizer needs to enable at least one KotH challenge.')}
            </Text>
          )}
        </Stack>
      </Paper>
    )
  }

  return (
    <Paper shadow="md" p="md">
      <Stack gap="xs">
        {/* Toolbar — identical layout to AdScoreboardTable so muscle memory carries over. */}
        <Grid>
          <Grid.Col span={3}>
            <Select
              defaultValue="all"
              data={[
                { value: 'all', label: t('game.label.score_table.all_teams', 'All teams') },
                ...divisionOptions,
              ]}
              value={selectValue}
              readOnly={!hasDivisionFilter}
              onChange={(div) => setDivisionName(!div || div === 'all' ? null : div)}
              leftSection={<Icon path={mdiAccountGroup} size={1} />}
            />
          </Grid.Col>
          <Grid.Col span={4}>
            {myTeamName && (
              <Button
                variant="light"
                leftSection={<Icon path={mdiCrosshairsGps} size={0.9} />}
                onClick={() => {
                  const idx = filteredList.findIndex((it) => it.teamName === myTeamName)
                  if (idx < 0) return
                  const page = Math.floor(idx / ITEM_COUNT_PER_PAGE) + 1
                  setPage(page)
                  setHighlightedTeam(myTeamName)
                  requestAnimationFrame(() => {
                    const el = document.querySelector(
                      `[data-team-name="${CSS.escape(myTeamName)}"]`
                    )
                    if (el) el.scrollIntoView({ behavior: 'smooth', block: 'center' })
                  })
                  setTimeout(() => setHighlightedTeam(null), 2500)
                }}
              >
                {t('game.button.find_my_team', 'Find My Team')}
              </Button>
            )}
          </Grid.Col>
          <Grid.Col span={2}>
            <Group justify="flex-end" gap="xs" h="100%">
              <Text size="xs" c="dimmed">
                {t('game.content.scoreboard.koth.latest_round', {
                  round: kothScoreboard.latestRound,
                  defaultValue: 'through round {{round}}',
                })}
              </Text>
            </Group>
          </Grid.Col>
          <Grid.Col span={3}>
            <TextInput
              placeholder={t('game.placeholder.search_team', 'Search Team')}
              value={keyword}
              onChange={(e) => setKeyword(e.currentTarget.value)}
              leftSection={<Icon path={mdiMagnify} size={1} />}
            />
          </Grid.Col>
        </Grid>

        <Box pos="relative" mih="calc(100vh - 14rem)">
          <Table.ScrollContainer
            minWidth="100%"
            classNames={{ scrollContainer: misc.noScrollBars }}
          >
            <Table className={classes.table} verticalSpacing={4} horizontalSpacing={8}>
              <Table.Thead className={classes.thead}>
                {/* Tier 1 — colored category bands (Web / Pwn / Crypto / Misc),
                    one cell per category group spanning its hills' 2 sub-columns
                    each. Matches AdScoreboardTable + ScoreboardTable so the
                    category-color cue is consistent across all three boards. */}
                <Table.Tr className={misc.noBorder}>
                  {hiddenCol}
                  {hillGroups.map((grp) => {
                    const cate = categoryLabelMap.get(grp.category as ChallengeCategory)
                    return (
                      <Table.Th
                        key={grp.category}
                        colSpan={grp.items.length * 2}
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
                            <Icon
                              path={cate.icon}
                              size={0.8}
                              color={theme.colors[cate.color][colorScheme === 'dark' ? 8 : 6]}
                            />
                          )}
                          <Text c={cate?.color} className={classes.text} ff="text" fz="xs">
                            {grp.category}
                          </Text>
                        </Group>
                      </Table.Th>
                    )
                  })}
                  <Table.Th rowSpan={3} aria-hidden />
                </Table.Tr>
                {/* Tier 2 — hill name (spans its 2 sub-columns) + current-holder pill.
                    Violet-themed since hills are KotH. */}
                <Table.Tr className={misc.noBorder}>
                  {hiddenCol}
                  {kothScoreboard.hills.map((hill) => (
                    <Table.Th
                      key={hill.challengeId}
                      colSpan={2}
                      className={classes.groupStart}
                      h="2.4rem"
                      style={{
                        backgroundColor: alpha(
                          theme.colors.violet[colorScheme === 'dark' ? 8 : 6],
                          colorScheme === 'dark' ? 0.12 : 0.14
                        ),
                      }}
                    >
                      <Group gap={4} wrap="nowrap" justify="center" w="100%">
                        <Icon
                          path={mdiCrown}
                          size={0.7}
                          color={theme.colors.violet[colorScheme === 'dark' ? 4 : 7]}
                        />
                        <Tooltip label={hill.title} withinPortal>
                          <Text c="violet" className={classes.text} ff="text" fz="xs" fw={700} truncate maw={GROUP_W - 24}>
                            {hill.title}
                          </Text>
                        </Tooltip>
                      </Group>
                    </Table.Th>
                  ))}
                </Table.Tr>
                {/* Tier 3 — pinned column labels + per-hill metric icons (hold / status). */}
                <Table.Tr>
                  {[
                    t('game.label.score_table.rank_total', 'Rank'),
                    t('game.label.score_table.rank_division', 'Division'),
                    t('common.label.team', 'Team'),
                    t('game.content.scoreboard.koth.column.ticks', 'Ticks'),
                    t('game.content.scoreboard.koth.column.total', 'Total'),
                  ].map((header, idx) => (
                    <Table.Th
                      key={idx}
                      className={cx(classes.left, classes.header)}
                      style={{ left: Lefts[idx] }}
                    >
                      {header}
                    </Table.Th>
                  ))}
                  {kothScoreboard.hills.flatMap((hill) => [
                    <Table.Th key={`${hill.challengeId}-p`} className={cx(classes.mono, classes.groupStart)} style={{ width: SUBCOL.pts }}>
                      <Tooltip
                        label={
                          hill.currentHolderTeamName
                            ? t('game.tooltip.koth.holder',
                                'Currently held by {{team}}', { team: hill.currentHolderTeamName })
                            : t('game.content.scoreboard.koth.legend.hold', 'Hold points')
                        }
                        withinPortal
                      >
                        <Center>
                          {hill.currentHolderTeamName ? (
                            <Badge
                              size="xs"
                              variant="filled"
                              color="violet"
                              leftSection={<Icon path={mdiCrown} size={0.4} />}
                              styles={{ root: { textTransform: 'none' }, label: { fontSize: 9 } }}
                            >
                              <Text size="xs" truncate maw={SUBCOL.pts - 30}>
                                {hill.currentHolderTeamName}
                              </Text>
                            </Badge>
                          ) : (
                            <Icon path={mdiCrown} size={0.55} color={theme.colors.violet[6]} />
                          )}
                        </Center>
                      </Tooltip>
                    </Table.Th>,
                    <Table.Th key={`${hill.challengeId}-st`} className={classes.mono} style={{ width: SUBCOL.status }}>
                      <Tooltip label={t('game.content.scoreboard.koth.column.status', 'Hill status')} withinPortal>
                        <Center>
                          <Badge
                            size="xs"
                            variant="light"
                            color={statusColor(hill.lastCheckStatus)}
                            px={5}
                            styles={{ root: { textTransform: 'none' }, label: { fontSize: 9 } }}
                          >
                            {hill.lastCheckStatus === 'InternalError'
                              ? 'Error'
                              : (hill.lastCheckStatus ?? t('game.content.scoreboard.ad.cell.no_check', 'n/a'))}
                          </Badge>
                        </Center>
                      </Tooltip>
                    </Table.Th>,
                  ])}
                </Table.Tr>
              </Table.Thead>
              <Table.Tbody>
                {currentItems.map((row, idx) => {
                  const isHighlighted = highlightedTeam === row.teamName
                  const tableRank = base + idx + 1
                  const totalTicks = row.hills.reduce((s, h) => s + (h.ticksHeld ?? 0), 0)
                  return (
                    <Table.Tr
                      key={row.participationId}
                      data-team-name={row.teamName}
                      style={
                        isHighlighted
                          ? {
                              outline: `2px solid ${theme.colors[theme.primaryColor][4]}`,
                              outlineOffset: -2,
                              background: alpha(theme.colors[theme.primaryColor][4], 0.12),
                              transition: 'background .3s ease, outline .3s ease',
                            }
                          : undefined
                      }
                    >
                      <Table.Td className={cx(classes.mono, classes.left)} style={{ left: Lefts[0] }}>
                        {row.rank || '-'}
                      </Table.Td>
                      <Table.Td className={cx(classes.mono, classes.left)} style={{ left: Lefts[1] }}>
                        {allRank ? row.rank : tableRank}
                      </Table.Td>
                      <Table.Td className={classes.left} style={{ left: Lefts[2] }}>
                        <Group justify="left" gap={5} wrap="nowrap" maw={Widths[2] - 10}>
                          <Avatar
                            imageProps={{ loading: 'lazy' }}
                            alt="avatar"
                            radius="xl"
                            size={30}
                            color={theme.primaryColor}
                          >
                            {row.teamName?.slice(0, 1) ?? 'T'}
                          </Avatar>
                          <Stack gap={0} h="2.5rem" justify="center" w={Widths[2] - 45}>
                            <ScrollingText size="sm" text={row.teamName || ''} />
                            {!!row.division && (
                              <Text
                                size="xs"
                                c="dimmed"
                                ta="start"
                                truncate
                                className={classes.text}
                              >
                                {row.division}
                              </Text>
                            )}
                          </Stack>
                        </Group>
                      </Table.Td>
                      <Table.Td className={cx(classes.mono, classes.left)} style={{ left: Lefts[3] }}>
                        {totalTicks}
                      </Table.Td>
                      <Table.Td className={cx(classes.mono, classes.left)} style={{ left: Lefts[4] }}>
                        {row.total.toFixed(1)}
                      </Table.Td>

                      {/* Per-hill cells — points + status. Highlight the cell violet when
                          this team is the current holder of THIS hill (matches the
                          "you hold it" affordance in the KothChallengePanel). */}
                      {kothScoreboard.hills.flatMap((hill) => {
                        const cell = row.hills.find((h) => h.challengeId === hill.challengeId)
                        const pts = cell?.points ?? 0
                        const ticks = cell?.ticksHeld ?? 0
                        const holding = cell?.isCurrentHolder ?? false
                        const tdStyle = holding ? {
                          background: alpha(theme.colors.violet[colorScheme === 'dark' ? 7 : 4], 0.18),
                        } : undefined
                        return [
                          <Table.Td
                            key={`${hill.challengeId}-p`}
                            className={cx(classes.mono, classes.groupStart)}
                            style={tdStyle}
                          >
                            <Tooltip
                              label={t('game.content.koth.ticks', '{{n}} ticks', { n: ticks })}
                              withinPortal
                              disabled={ticks === 0}
                            >
                              <Text
                                size="xs"
                                c={holding ? 'violet' : pts > 0 ? undefined : 'dimmed'}
                                className={misc.ffmono}
                                fw={holding ? 800 : 700}
                              >
                                {pts > 0 ? fmtPts(pts) : '0'}
                              </Text>
                            </Tooltip>
                          </Table.Td>,
                          <Table.Td
                            key={`${hill.challengeId}-st`}
                            className={classes.mono}
                            style={tdStyle}
                          >
                            {holding ? (
                              <Center>
                                <Icon path={mdiCrown} size={0.55} color={theme.colors.violet[colorScheme === 'dark' ? 4 : 7]} />
                              </Center>
                            ) : null}
                          </Table.Td>,
                        ]
                      })}
                      <Table.Td aria-hidden />
                    </Table.Tr>
                  )
                })}
              </Table.Tbody>
            </Table>
          </Table.ScrollContainer>

          {filteredList.length === 0 && (
            <Center mih="6rem">
              <Text size="sm" c="dimmed">
                {t('game.content.scoreboard.koth.no_teams', 'No teams ranked yet.')}
              </Text>
            </Center>
          )}
        </Box>

        {/* Footer — legend on the left, pagination on the right, mirroring AD. */}
        <Group justify="space-between" align="flex-start" wrap="wrap" gap="md">
          <Stack gap={4}>
            <Tooltip.Group>
              <Group gap="lg">
                <Tooltip
                  label={t('game.content.scoreboard.koth.legend.hold_tip',
                    'Points per tick while your token is in /koth/king (scaled by team count).')}
                  transitionProps={{ transition: 'pop' }}
                >
                  <Group justify="left" gap={4}>
                    <Icon path={mdiCrown} size={0.8} color={theme.colors.violet[6]} />
                    <Text size="sm" c="violet">
                      {t('game.content.scoreboard.koth.legend.hold', 'Hold points')}
                    </Text>
                  </Group>
                </Tooltip>
                <Tooltip
                  label={t('game.content.scoreboard.koth.legend.status_tip',
                    'Functional probe verdict on the hill — broken hills cost the holder a flat penalty.')}
                  transitionProps={{ transition: 'pop' }}
                >
                  <Group justify="left" gap={4}>
                    <Icon path={mdiHeartPulse} size={0.8} color="var(--mantine-color-dimmed)" />
                    <Text size="sm" c="dimmed">
                      {t('game.content.scoreboard.koth.legend.status', 'Hill status')}
                    </Text>
                  </Group>
                </Tooltip>
                <Tooltip
                  label={t('game.content.scoreboard.koth.legend.refresh_tip',
                    'Each hill is wiped + redeployed every few ticks, so footholds + patches don\'t accumulate.')}
                  transitionProps={{ transition: 'pop' }}
                >
                  <Group justify="left" gap={4}>
                    <Icon path={mdiTimerSandComplete} size={0.8} color={theme.colors.blue[6]} />
                    <Text size="sm" c="blue">
                      {t('game.content.scoreboard.koth.legend.refresh', '5-tick refresh')}
                    </Text>
                  </Group>
                </Tooltip>
              </Group>
            </Tooltip.Group>
            <Text size="xs" c="dimmed">
              {t('game.content.scoreboard.koth.tip',
                'Total = Σ (hold credit − penalty) across all hills. Updated after every check.')}
            </Text>
          </Stack>
          <Pagination
            value={activePage}
            onChange={setPage}
            total={Math.max(1, Math.ceil(filteredList.length / ITEM_COUNT_PER_PAGE))}
            boundaries={2}
          />
        </Group>
      </Stack>
    </Paper>
  )
}
