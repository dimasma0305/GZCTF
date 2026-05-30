import {
  alpha,
  Badge,
  Box,
  Center,
  Group,
  Pagination,
  Paper,
  Stack,
  Table,
  Text,
  Tooltip,
  useMantineColorScheme,
  useMantineTheme,
} from '@mantine/core'
import { mdiCrown, mdiHeartPulse, mdiTimerSandComplete } from '@mdi/js'
import { Icon } from '@mdi/react'
import cx from 'clsx'
import { FC, useMemo } from 'react'
import { useTranslation } from 'react-i18next'
import {
  AdLikeCategoryHeaderRow,
  AdLikeHiddenCols,
  AdLikePinnedHeaderCells,
  AdLikePinnedRowCells,
  AdLikeStatusLegend,
  AdLikeToolbar,
  ITEM_COUNT_PER_PAGE,
  adLikeRowHighlight,
  fmtPts,
  statusBg,
  useAdLikeScoreboardState,
} from '@Components/AdLikeScoreboard'
import { useGame, useKothScoreboard, type KothScoreboardHill } from '@Hooks/useGame'
import misc from '@Styles/Misc.module.css'
import classes from '@Styles/ScoreboardTable.module.css'

// One sub-column per hill: hold points (the +earned / −penalty breakdown side
// by side, net below). Status has no column of its own — it's the cell
// background tint (see statusBg); the current holder is the violet wash + a
// crown by the net + the header pill.
const SUBCOL = { pts: 140 }
const GROUP_W = SUBCOL.pts

interface KothScoreboardTableProps {
  numId: number
}

/**
 * King of the Hill — dedicated scoreboard. Shares the toolbar / pinned-left
 * columns / category tier / highlight / footer-pagination scaffolding with
 * AdScoreboardTable (see AdLikeScoreboard), and differs only in the per-hill
 * column: one hold-points sub-cell tinted by the hill's status, with the
 * current holder marked by a violet wash + the header pill.
 */
export const KothScoreboardTable: FC<KothScoreboardTableProps> = ({ numId }) => {
  const { t } = useTranslation()
  const theme = useMantineTheme()
  const { colorScheme } = useMantineColorScheme()
  const { kothScoreboard, error: kothError } = useKothScoreboard(numId)
  const { game } = useGame(numId)
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

  const {
    activePage,
    setPage,
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
  } = useAdLikeScoreboardState(kothScoreboard?.teams, myTeamName)

  if (!kothScoreboard || kothScoreboard.hills.length === 0) {
    return (
      <Paper shadow="md" p="xl">
        <Stack align="center" gap="xs">
          <Icon path={mdiCrown} size={2.5} color="var(--mantine-color-dimmed)" />
          <Text fw="bold" c="dimmed">
            {!kothScoreboard
              ? kothError
                ? t('game.content.scoreboard.koth.error', 'Could not load the King of the Hill board.')
                : t('game.content.scoreboard.koth.loading', 'Loading King of the Hill…')
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
        <AdLikeToolbar
          divisionOptions={divisionOptions}
          selectValue={selectValue}
          hasDivisionFilter={hasDivisionFilter}
          onDivisionChange={setDivisionName}
          myTeamName={myTeamName}
          onFindMyTeam={findMyTeam}
          keyword={keyword}
          onKeywordChange={setKeyword}
          latestRoundText={t('game.content.scoreboard.koth.latest_round', {
            round: kothScoreboard.latestRound,
            defaultValue: 'through round {{round}}',
          })}
        />

        <Box pos="relative" mih="calc(100vh - 14rem)">
          <Table.ScrollContainer minWidth="100%" classNames={{ scrollContainer: misc.noScrollBars }}>
            <Table className={classes.table} verticalSpacing={4} horizontalSpacing={8}>
              <Table.Thead className={classes.thead}>
                {/* Tier 1 — colored category bands (shared with A&D board). */}
                <AdLikeCategoryHeaderRow groups={hillGroups} subColsPerItem={1} />
                {/* Tier 2 — hill name + current-holder pill. Violet-themed since
                    hills are KotH. */}
                <Table.Tr className={misc.noBorder}>
                  <AdLikeHiddenCols />
                  {kothScoreboard.hills.map((hill) => (
                    <Table.Th
                      key={hill.challengeId}
                      colSpan={1}
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
                {/* Tier 3 — pinned column labels + per-hill holder pill. Status
                    has no column — it's the cell tint. */}
                <Table.Tr>
                  <AdLikePinnedHeaderCells
                    countLabel={t('game.content.scoreboard.koth.column.ticks', 'Ticks')}
                    totalLabel={t('game.content.scoreboard.koth.column.total', 'Total')}
                  />
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
                      style={adLikeRowHighlight(isHighlighted, theme)}
                    >
                      {/* Pinned columns — shared with A&D board. Ticks held is the
                          KotH "count" column; Total is the headline rollup. */}
                      <AdLikePinnedRowCells
                        rank={row.rank}
                        teamName={row.teamName}
                        division={row.division}
                        total={row.total}
                        allRank={allRank}
                        tableRank={tableRank}
                        countValue={totalTicks}
                      />

                      {/* Per-hill cell — points (broken into +earned / −penalty
                          side by side, net below), tinted by the hill's status.
                          Violet wash + a crown by the net when the team holds it. */}
                      {kothScoreboard.hills.flatMap((hill) => {
                        const cell = row.hills.find((h) => h.challengeId === hill.challengeId)
                        const earned = cell?.earned ?? 0
                        const penalty = cell?.penalty ?? 0
                        const net = cell?.points ?? 0
                        const ticks = cell?.ticksHeld ?? 0
                        const broken = cell?.brokenTicks ?? 0
                        const holding = cell?.isCurrentHolder ?? false
                        // Holder keeps the violet wash (who holds the hill is the
                        // key KotH signal); every other cell gets the hill's
                        // status tint, matching the A&D board. The hill is shared,
                        // so the tint is uniform down a column (= hill health).
                        const tdStyle = holding
                          ? { background: alpha(theme.colors.violet[colorScheme === 'dark' ? 7 : 4], 0.18) }
                          : { backgroundColor: statusBg(hill.lastCheckStatus, theme, colorScheme === 'dark') }
                        return [
                          <Table.Td
                            key={`${hill.challengeId}-p`}
                            className={cx(classes.mono, classes.groupStart)}
                            style={tdStyle}
                          >
                            <Tooltip
                              label={
                                ticks === 0
                                  ? ''
                                  : penalty > 0
                                    ? t('game.tooltip.koth.cell.with_penalty',
                                        '{{ticks}} ticks held — earned {{earned}}, broken {{broken}} ticks (penalty −{{penalty}}) → net {{net}}',
                                        { ticks, earned: earned.toFixed(1), broken, penalty: penalty.toFixed(1), net: net.toFixed(1) })
                                    : t('game.tooltip.koth.cell.no_penalty',
                                        '{{ticks}} ticks held — earned {{earned}}, hill stayed Ok', { ticks, earned: earned.toFixed(1) })
                              }
                              withinPortal
                              disabled={ticks === 0}
                            >
                              <Stack gap={0} align="center">
                                <Group gap={4} wrap="nowrap" justify="center">
                                  <Text size="xs" c="teal" className={misc.ffmono} fw={700}>
                                    {earned > 0 ? `+${fmtPts(earned)}` : '0'}
                                  </Text>
                                  <Text size="xs" c={penalty > 0 ? 'red' : 'dimmed'} className={misc.ffmono} fw={700}>
                                    {penalty > 0 ? `−${fmtPts(penalty)}` : '−0'}
                                  </Text>
                                </Group>
                                {ticks > 0 && (
                                  <Group gap={2} wrap="nowrap" justify="center">
                                    {holding && (
                                      <Icon path={mdiCrown} size={0.4} color={theme.colors.violet[colorScheme === 'dark' ? 4 : 7]} />
                                    )}
                                    <Text size="xs" c={holding ? 'violet' : 'dimmed'} className={misc.ffmono} fw={holding ? 800 : 600} style={{ fontSize: 9, lineHeight: 1 }}>
                                      = {fmtPts(net)}
                                    </Text>
                                  </Group>
                                )}
                              </Stack>
                            </Tooltip>
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

          {/* Status color key, floated over the empty top-left header cells. */}
          <AdLikeStatusLegend />

          {filteredList.length === 0 && (
            <Center mih="6rem">
              <Text size="sm" c="dimmed">
                {t('game.content.scoreboard.koth.no_teams', 'No teams ranked yet.')}
              </Text>
            </Center>
          )}
        </Box>

        {/* Footer — legend on the left, pagination on the right, mirroring A&D. */}
        <Group justify="space-between" align="flex-start" wrap="wrap" gap="md">
          <Stack gap={4}>
            <Tooltip.Group>
              <Group gap="lg">
                <Tooltip
                  label={t('game.content.scoreboard.koth.legend.earned_tip',
                    'Per-tick credit while your token is in /koth/king AND the hill is Ok. Flat — does NOT scale with team count.')}
                  transitionProps={{ transition: 'pop' }}
                >
                  <Group justify="left" gap={4}>
                    <Text size="sm" c="teal" fw={700} ff="monospace">+</Text>
                    <Text size="sm" c="teal">
                      {t('game.content.scoreboard.koth.legend.earned', 'Hold credit')}
                    </Text>
                  </Group>
                </Tooltip>
                <Tooltip
                  label={t('game.content.scoreboard.koth.legend.penalty_tip',
                    'Flat −1 per tick when you hold a broken hill (Mumble / Offline). InternalError (a checker/infra fault) is never charged. One-tick grace on takeover so previous-holder damage isn\'t your fault.')}
                  transitionProps={{ transition: 'pop' }}
                >
                  <Group justify="left" gap={4}>
                    <Text size="sm" c="red" fw={700} ff="monospace">−</Text>
                    <Text size="sm" c="red">
                      {t('game.content.scoreboard.koth.legend.penalty', 'Broken-hill penalty')}
                    </Text>
                  </Group>
                </Tooltip>
                <Tooltip
                  label={t('game.content.scoreboard.koth.legend.holder_tip',
                    'Crown marks the team currently holding the hill (last persisted check).')}
                  transitionProps={{ transition: 'pop' }}
                >
                  <Group justify="left" gap={4}>
                    <Icon path={mdiCrown} size={0.8} color={theme.colors.violet[6]} />
                    <Text size="sm" c="violet">
                      {t('game.content.scoreboard.koth.legend.holder', 'Current holder')}
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
                'Each cell shows +earned and −penalty side by side; the team Total is the sum of (earned − penalty) across all hills. Updated after every check.')}
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
